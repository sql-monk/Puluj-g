using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Puluj.Analytics.Reporting;
using Puluj.Admin.Docker;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.Admin;

/// <summary>
/// Operations view of the system for the admin panel: which service is alive, what every instance reports about
/// itself, what each collector does, how the pipeline keeps up, the containers of the compose stack, what the database
/// holds, and the tail of every service's log. Statistics come from SQL, statuses from `app_settings`, containers from
/// the docker CLI (<see cref="DockerService"/>); the only writes are the container actions and the reprocess request.
/// </summary>
public static partial class OpsEndpoints
{
    private static readonly TimeSpan WorkerStale = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WorkerForgotten = TimeSpan.FromMinutes(15); // a killed dev process leaves its key behind

    /// <summary>The Worker writes its status document in camelCase (the web JSON options); enums as strings.</summary>
    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/admin").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        ops.MapGet("/ops/overview", OverviewAsync);
        ops.MapGet("/ops/workers", WorkersAsync);
        ops.MapGet("/ops/collectors", CollectorsAsync);
        ops.MapGet("/ops/pipeline", PipelineAsync);
        ops.MapGet("/ops/llm", LlmAsync);
        ops.MapGet("/ops/llm/requests/{id:long}", LlmRequestAsync);
        ops.MapGet("/ops/db", DbAsync);
        ops.MapGet("/ops/db/tables/{name}/rows", DbTableRowsAsync);
        ops.MapPost("/ops/db/query", DbQueryAsync);

        // Containers of the compose stack (docs/plan-admin-ops.md §2.3): list, restart / stop / start, scale the processors.
        ops.MapGet("/ops/containers", async (DockerService docker, CancellationToken ct) => Results.Ok(await docker.ListAsync(ct)));
        ops.MapPost("/ops/containers/{id}/{action:regex(^(restart|stop|start)$)}", async (string id, string action, HttpContext http, DockerService docker, CancellationToken ct) =>
        {
            var outcome = await docker.ActAsync(id, action, http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Json(outcome.Result, statusCode: outcome.StatusCode);
        });
        ops.MapPost("/ops/processors/scale", async (ScaleRequest req, HttpContext http, DockerService docker, CancellationToken ct) =>
        {
            var outcome = await docker.ScaleAsync(req.Replicas, http.Connection.RemoteIpAddress?.ToString(), ct);
            var containers = outcome.StatusCode is 200 or 502 ? await docker.ListAsync(ct, fresh: true) : null;
            return Results.Json(new { result = outcome.Result, containers }, statusCode: outcome.StatusCode);
        });

        // Earned rating of the sources (originality, who copies whom, groups) with per-day history.
        ops.MapGet("/sources/rating", async (int? days, SnapshotService snapshots, CancellationToken ct) =>
            await snapshots.SourceRatingAsync(days ?? 14, ct));

        ops.MapGet("/logs/files", (LogReader logs) => Results.Ok(logs.Files()));
        ops.MapGet("/logs", (string file, int? lines, string? filter, string? level, LogReader logs) =>
        {
            try
            {
                return Results.Ok(logs.Tail(file, lines ?? 200, filter, level));
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
        });

        // Rebuild everything derived from the raw messages (after a parser / linker change): the Worker re-runs the
        // pipeline over all of them in publication order. Refused while a history load holds processing.
        ops.MapPost("/ops/reprocess", async (DbReprocessRequest request, ReprocessService reprocess, AnalyticsReportService analytics, CancellationToken ct) =>
        {
            if (!string.Equals(request.Confirmation, "REPROCESS_DERIVED_DATA", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Для цієї операції потрібне точне підтвердження." });
            }
            if (await reprocess.PausedAsync(ct) is { } paused)
            {
                return Results.Conflict(new { error = $"Обробку призупинено: {paused}" });
            }
            var queued = await reprocess.ResetAsync(ct);
            // The analytics schema is another derived view of raw_messages. It can be absent before its worker first starts.
            var analyticsStatus = await analytics.StatusAsync(ct);
            if (analyticsStatus.Initialized)
            {
                await analytics.ResetAsync(ct);
            }
            return Results.Ok(new { queued, analyticsReset = analyticsStatus.Initialized });
        });

        // Testing without real sources: inject a message as if a collector had received it. Takes the same path as the
        // collectors: the single ingress when Messaging:Ingress:Enabled (the raw-writer stores it), the direct store otherwise.
        ops.MapPost("/dev/ingest", async (IngestRequest req, ReferenceCache refs, RawMessageIngestor ingestor, IngressWriter ingress, TimeProvider clock, CancellationToken ct) =>
        {
            var source = refs.Sources.Values.FirstOrDefault(s => s.Code == req.SourceCode);
            if (source is null)
            {
                return Results.BadRequest(new { error = $"unknown source '{req.SourceCode}'" });
            }
            var message = new IncomingMessage
            {
                SourceId = source.SourceId,
                SourceMessageId = req.SourceMessageId ?? $"dev-{Guid.NewGuid():N}",
                PublishedAt = req.PublishedAt ?? clock.GetUtcNow(),
                RawText = string.IsNullOrWhiteSpace(req.Text) ? null : req.Text,
                RawPayload = req.Payload is { ValueKind: JsonValueKind.Object } p
                    ? JsonDocument.Parse(p.GetRawText())
                    : JsonDocument.Parse("{\"kind\":\"dev.ingest\"}"),
                Url = null,
            };
            if (ingress.Enabled)
            {
                var published = await ingress.PublishAsync(message, source.Code, "dev", null, live: true, ct);
                return Results.Ok(new { published.EventId, published.Lane, RawMessageId = (long?)null, IsNew = (bool?)null });
            }
            var result = await ingestor.IngestAsync(message, source.Code, ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static async Task<IResult> LlmAsync(int? hours, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct)
    {
        var span = hours ?? 168;
        if (!PipelineBuckets.AllowedHours.Contains(span))
        {
            return Results.BadRequest(new { error = "Період має бути 24, 168 або 720 годин." });
        }
        var to = clock.GetUtcNow();
        var from = to - TimeSpan.FromHours(span);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.LlmRequests.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt <= to);
        var totals = await query.GroupBy(_ => 1).Select(g => new
        {
            Calls = g.LongCount(),
            WithFacts = g.LongCount(x => x.Outcome == "facts"),
            Empty = g.LongCount(x => x.Outcome == "empty"),
            Refusals = g.LongCount(x => x.Outcome == "refusal"),
            Failures = g.LongCount(x => x.Outcome == "429" || x.Outcome == "api_error" || x.Outcome == "error"),
            Input = g.Sum(x => x.InputTokens ?? 0),
            CacheWrite = g.Sum(x => x.CacheCreationInputTokens ?? 0),
            CacheRead = g.Sum(x => x.CacheReadInputTokens ?? 0),
            Output = g.Sum(x => x.OutputTokens ?? 0),
            Cost = g.Sum(x => x.EstimatedCostUsd ?? 0m),
            MeanDuration = g.Average(x => (double?)x.DurationMs),
        }).FirstOrDefaultAsync(ct);
        var timeline = await query.GroupBy(x => x.OccurredAt.Date).OrderBy(g => g.Key).Select(g => new
        {
            At = g.Key,
            Calls = g.LongCount(),
            Input = g.Sum(x => x.InputTokens ?? 0),
            Output = g.Sum(x => x.OutputTokens ?? 0),
            Cost = g.Sum(x => x.EstimatedCostUsd ?? 0m),
        }).ToListAsync(ct);
        var recentRows = await query.OrderByDescending(x => x.LlmRequestId).Take(100).Select(x => new
        {
            x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, SourceCode = x.Source!.Code, x.Worker, x.Model, x.PromptVersion,
            x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens,
            x.EstimatedCostUsd, x.FactsCount, x.Error,
        }).ToListAsync(ct);
        return Results.Ok(new LlmUsageReportDto(from, to,
            totals?.Calls ?? 0, totals?.WithFacts ?? 0, totals?.Empty ?? 0, totals?.Refusals ?? 0, totals?.Failures ?? 0,
            totals?.Input ?? 0, totals?.CacheWrite ?? 0, totals?.CacheRead ?? 0, totals?.Output ?? 0, totals?.Cost ?? 0m, totals?.MeanDuration,
            timeline.Select(x => new LlmUsageBucketDto(new DateTimeOffset(x.At, TimeSpan.Zero), x.Calls, x.Input, x.Output, x.Cost)).ToList(),
            recentRows.Select(x => new LlmRequestDto(x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, x.SourceCode, x.Worker, x.Model, x.PromptVersion,
                x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens, x.EstimatedCostUsd, x.FactsCount, x.Error)).ToList()));
    }

    private static async Task<IResult> LlmRequestAsync(long id, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var x = await db.LlmRequests.AsNoTracking().Where(x => x.LlmRequestId == id).Select(x => new
        {
            x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, SourceCode = x.Source!.Code, x.Worker, x.Model, x.PromptVersion,
            x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens,
            x.EstimatedCostUsd, x.FactsCount, x.Error, x.RequestText, x.SystemPrompt, x.ResponseText,
            x.RequestId, x.RunId, x.FencingToken, x.AttemptId, x.ProviderRequestId,
            RequestPayload = x.RequestPayload, ResponsePayload = x.ResponsePayload, // verbatim bodies (what was sent, what came back)
        }).FirstOrDefaultAsync(ct);
        if (x is null)
        {
            return Results.NotFound();
        }
        var row = new LlmRequestDto(x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, x.SourceCode, x.Worker, x.Model, x.PromptVersion,
            x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens, x.EstimatedCostUsd, x.FactsCount, x.Error);
        return Results.Ok(new LlmRequestDetailDto(row, x.RequestText, x.SystemPrompt, x.ResponseText));
    }

    private static async Task<IResult> OverviewAsync(
        SettingsStore settings, IDbContextFactory<PulujDbContext> factory, IHttpClientFactory http, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var services = new List<ServiceStatusDto>();

        // Worker instances: each writes its own heartbeat into app_settings every 30 s (one container per role in Docker).
        var all = await settings.GetAllAsync(ct);
        var workers = WorkerHeartbeats(all, now);
        if (workers.Count == 0)
        {
            services.Add(new ServiceStatusDto("worker", "unknown", "heartbeat ще не записано", null));
        }
        var processors = 0;
        foreach (var (name, heartbeat) in workers)
        {
            var alive = now - heartbeat < WorkerStale;
            if (alive && DockerContainers.KindOf(name) == "processor")
            {
                processors++;
            }
            services.Add(new ServiceStatusDto($"worker:{name}", alive ? "ok" : "down",
                alive ? "heartbeat свіжий" : $"heartbeat застарів на {(int)(now - heartbeat).TotalMinutes} хв", heartbeat));
        }

        // Api: its own health endpoint over HTTP.
        var apiUrl = (config["Admin:ApiUrl"] ?? "http://localhost:5267").TrimEnd('/');
        try
        {
            var client = http.CreateClient("api-probe");
            client.Timeout = TimeSpan.FromSeconds(5);
            using var res = await client.GetAsync($"{apiUrl}/api/health", ct);
            services.Add(new ServiceStatusDto("api", res.IsSuccessStatusCode ? "ok" : "warn", $"{apiUrl}/api/health → {(int)res.StatusCode}", now));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            services.Add(new ServiceStatusDto("api", "down", $"{apiUrl}: {e.Message}", null));
        }

        // Collectors: the aggregate of the enabled sources.
        await using var db = await factory.CreateDbContextAsync(ct);
        var collectors = await db.CollectorStates.AsNoTracking().Include(c => c.Source).Where(c => c.Source!.Enabled).ToListAsync(ct);
        var failing = collectors.Count(c => c.ConsecutiveFailures > 0);
        var lastSuccess = collectors.Max(c => c.LastSuccessAt);
        services.Add(new ServiceStatusDto("collectors",
            collectors.Count == 0 ? "unknown" : failing == 0 ? "ok" : failing == collectors.Count ? "down" : "warn",
            $"{collectors.Count} увімкнених, {failing} з помилками", lastSuccess));

        // Telegram session (the worker keeps its status in app_settings).
        var tgStatus = all.TryGetValue("Runtime:Telegram:Status", out var ts) ? ts.Value : null;
        services.Add(new ServiceStatusDto("telegram", tgStatus is null ? "unknown" : tgStatus.Contains("ok", StringComparison.OrdinalIgnoreCase) || tgStatus.Contains("connected", StringComparison.OrdinalIgnoreCase) || tgStatus.StartsWith("listening", StringComparison.OrdinalIgnoreCase) ? "ok" : "warn", tgStatus, null));

        // Database.
        var version = (await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").ToListAsync(ct)).FirstOrDefault() ?? "?";
        var size = (await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").ToListAsync(ct)).FirstOrDefault();
        var connections = (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()").ToListAsync(ct)).FirstOrDefault();
        var migrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        services.Add(new ServiceStatusDto("postgres", "ok", version.Split(' ', 3) is { Length: >= 2 } v ? $"{v[0]} {v[1]}" : version, now));
        services.Add(new ServiceStatusDto("admin", "ok", "ця панель", now));

        return Results.Ok(new OpsOverviewDto(now, services, new DbOverviewDto(version, size, connections, migrations.LastOrDefault(), migrations.Count), processors));
    }

    /// <summary>
    /// Heartbeats of the Worker instances (`Runtime:Worker:{name}:Heartbeat`), oldest first. An instance removes its key on a clean
    /// shutdown; one that died leaves a stale value, which is shown as down for a while and then forgotten.
    /// </summary>
    public static List<(string Name, DateTimeOffset At)> WorkerHeartbeats(IReadOnlyDictionary<string, AppSetting> all, DateTimeOffset now)
    {
        var result = new List<(string, DateTimeOffset)>();
        foreach (var (key, setting) in all)
        {
            const string prefix = "Runtime:Worker:", suffix = ":Heartbeat";
            // The pre-roles key `Runtime:Worker:Heartbeat` (no instance name) is skipped, not parsed.
            if (key.Length <= prefix.Length + suffix.Length
                || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var name = key[prefix.Length..^suffix.Length];
            if (DateTimeOffset.TryParse(setting.Value, out var at) && now - at < WorkerForgotten)
            {
                result.Add((name, at));
            }
        }
        return result.OrderBy(w => w.Item2).ToList();
    }

    /// <summary>The instance's own status document (`Runtime:Worker:{name}:Status`, §2.1); null when absent or unreadable.</summary>
    public static WorkerStatusDto? WorkerStatus(IReadOnlyDictionary<string, AppSetting> all, string name)
    {
        var key = $"Runtime:Worker:{name}:Status";
        var value = all.TryGetValue(key, out var exact) ? exact.Value
            : all.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WorkerStatusDto>(value, StatusJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ClaimRow(string ClaimedBy, long ProcessedDay, long InProgress);

    /// <summary>Every instance with a heartbeat: its status document, its share of the work (by claimed_by) and its container.</summary>
    private static async Task<IResult> WorkersAsync(SettingsStore settings, IDbContextFactory<PulujDbContext> factory, DockerService docker, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await settings.GetAllAsync(ct);
        var heartbeats = WorkerHeartbeats(all, now);
        await using var db = await factory.CreateDbContextAsync(ct);
        var claims = (await db.Database.SqlQueryRaw<ClaimRow>("""
            SELECT claimed_by AS claimed_by,
                   count(*) FILTER (WHERE processed_at >= now() - interval '24 hours' AND processing_status = 1) AS processed_day,
                   count(*) FILTER (WHERE processing_status = 4) AS in_progress
            FROM raw_messages
            WHERE claimed_by IS NOT NULL AND (processed_at >= now() - interval '24 hours' OR processing_status = 4)
            GROUP BY 1
            """).ToListAsync(ct)).ToDictionary(c => c.ClaimedBy, StringComparer.OrdinalIgnoreCase);
        var containers = docker.Enabled ? (await docker.ListAsync(ct)).Containers : [];

        var list = heartbeats.Select(h =>
        {
            var kind = DockerContainers.KindOf(h.Name);
            var claim = claims.GetValueOrDefault(h.Name);
            var container = DockerContainers.Match(h.Name, kind, containers);
            return new WorkerInstanceDto(h.Name, kind, now - h.At < WorkerStale, h.At, WorkerStatus(all, h.Name),
                claim?.ProcessedDay ?? 0, claim?.InProgress ?? 0,
                container?.Id, container?.Name, container?.State, container?.CpuPercent, container?.MemoryBytes);
        })
        .OrderBy(w => KindOrder(w.Kind)).ThenBy(w => w.Name, StringComparer.Ordinal)
        .ToList();
        return Results.Ok(list);
    }

    private static int KindOrder(string kind) => kind switch
    {
        "processor" => 0,
        "collector-telegram" => 1,
        "collector-alerts" => 2,
        "analytics" => 3,
        "worker" => 4,
        _ => 5,
    };

    private static async Task<IResult> PipelineAsync(int? hours, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct)
    {
        var h = hours ?? 24;
        if (!PipelineBuckets.AllowedHours.Contains(h))
        {
            return Results.BadRequest(new { error = $"hours має бути одним із: {string.Join(", ", PipelineBuckets.AllowedHours)}" });
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        return Results.Ok(await PipelineReport.BuildAsync(db, h, clock.GetUtcNow(), ct));
    }

    private sealed record HourCount(int SourceId, DateTime Hour, int Count);

    private static async Task<IResult> CollectorsAsync(IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var firstHour = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc).AddHours(-23);
        var sources = await db.Sources.AsNoTracking().OrderByDescending(s => s.Enabled).ThenByDescending(s => s.Priority).ToListAsync(ct);
        var states = await db.CollectorStates.AsNoTracking().ToDictionaryAsync(c => c.SourceId, ct);
        var counts = await db.Database.SqlQuery<HourCount>($"""
            SELECT source_id, date_trunc('hour', received_at)::timestamp AS hour, count(*)::int AS count
            FROM raw_messages
            WHERE received_at >= {firstHour}
            GROUP BY 1, 2
            """).ToListAsync(ct);
        var byId = counts.ToLookup(c => c.SourceId);

        var list = sources.Select(s =>
        {
            states.TryGetValue(s.SourceId, out var st);
            var perHour = new int[24];
            foreach (var c in byId[s.SourceId])
            {
                var i = (int)Math.Floor((DateTime.SpecifyKind(c.Hour, DateTimeKind.Utc) - firstHour).TotalHours);
                if (i is >= 0 and < 24)
                {
                    perHour[i] += c.Count;
                }
            }
            return new CollectorStatusDto(s.SourceId, s.Code, s.Name, s.Type.ToString(), s.Enabled,
                st?.LastPolledAt, st?.LastSuccessAt, st?.LastMessageAt, st?.LastError, st?.ConsecutiveFailures ?? 0,
                perHour.Sum(), perHour);
        }).ToList();
        return Results.Ok(list);
    }

    private sealed record TableRow(
        string Name, long Rows, long Bytes, long Inserts, long Updates, long Deletes, long DeadRows,
        DateTimeOffset? LastVacuumAt, DateTimeOffset? LastAnalyzeAt);
    private sealed record RoleRow(string Role, int Connections);
    private sealed record MonitoringRow(int ActiveConnections, int IdleConnections, long TransactionsCommitted, long TransactionsRolledBack, double CacheHitRatio, long DeadRows);

    private static async Task<IResult> DbAsync(IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var version = (await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").ToListAsync(ct)).FirstOrDefault() ?? "?";
        var size = (await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").ToListAsync(ct)).FirstOrDefault();
        var tables = await db.Database.SqlQueryRaw<TableRow>("""
            SELECT relname AS name, n_live_tup AS rows, pg_total_relation_size(relid) AS bytes,
                   n_tup_ins AS inserts, n_tup_upd AS updates, n_tup_del AS deletes, n_dead_tup AS dead_rows,
                   coalesce(last_autovacuum, last_vacuum) AS last_vacuum_at,
                   coalesce(last_autoanalyze, last_analyze) AS last_analyze_at
            FROM pg_stat_user_tables WHERE schemaname = 'public' ORDER BY bytes DESC
            """).ToListAsync(ct);
        var roles = await db.Database.SqlQueryRaw<RoleRow>("""
            SELECT coalesce(usename, '?') AS role, count(*)::int AS connections
            FROM pg_stat_activity WHERE datname = current_database() GROUP BY 1 ORDER BY 2 DESC
            """).ToListAsync(ct);
        var monitoring = (await db.Database.SqlQueryRaw<MonitoringRow>("""
            SELECT
              count(*) FILTER (WHERE state = 'active')::int AS active_connections,
              count(*) FILTER (WHERE state = 'idle')::int AS idle_connections,
              coalesce((SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()), 0) AS transactions_committed,
              coalesce((SELECT xact_rollback FROM pg_stat_database WHERE datname = current_database()), 0) AS transactions_rolled_back,
              coalesce((SELECT blks_hit::double precision / nullif(blks_hit + blks_read, 0) FROM pg_stat_database WHERE datname = current_database()), 1) AS cache_hit_ratio,
              (SELECT coalesce(sum(n_dead_tup), 0) FROM pg_stat_user_tables WHERE schemaname = 'public') AS dead_rows
            FROM pg_stat_activity WHERE datname = current_database()
            """).ToListAsync(ct)).Single();
        var migrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        return Results.Ok(new DbReportDto(version, size,
            tables.Select(t => new DbTableDto(t.Name, t.Rows, t.Bytes, t.Inserts, t.Updates, t.Deletes, t.DeadRows, t.LastVacuumAt, t.LastAnalyzeAt)).ToList(),
            migrations,
            roles.Select(r => new DbRoleConnectionsDto(r.Role, r.Connections)).ToList(),
            new DbMonitoringDto(monitoring.ActiveConnections, monitoring.IdleConnections, monitoring.TransactionsCommitted,
                monitoring.TransactionsRolledBack, monitoring.CacheHitRatio, monitoring.DeadRows)));
    }

    private static async Task<IResult> DbTableRowsAsync(string name, int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!TableName().IsMatch(name))
        {
            return Results.BadRequest(new { error = "Некоректна назва таблиці." });
        }
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Database.SqlQueryRaw<string>("""
            SELECT relname AS "Value" FROM pg_stat_user_tables
            WHERE schemaname = 'public' AND relname = {0}
            """, name).AnyAsync(ct);
        if (!exists)
        {
            return Results.NotFound(new { error = "Таблицю не знайдено." });
        }
        return Results.Ok(await ReadQueryAsync(db, $"SELECT * FROM public.\"{name}\" LIMIT {take + 1}", take, redactSensitiveColumns: true, ct));
    }

    private static async Task<IResult> DbQueryAsync(DbQueryRequest request, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!DatabaseQueryGuard.TryValidate(request.Sql, out var error))
        {
            return Results.BadRequest(new { error });
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        return Results.Ok(await ReadQueryAsync(db, request.Sql, 200, redactSensitiveColumns: false, ct));
    }

    private static async Task<DbQueryResultDto> ReadQueryAsync(PulujDbContext db, string sql, int maxRows, bool redactSensitiveColumns, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10s'", ct);
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        List<string> columns;
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        // Dispose the reader before rolling back the read-only transaction. In particular,
        // the table browser reads one extra row to detect truncation, leaving the result set
        // active when it has more than maxRows rows.
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var sensitive = redactSensitiveColumns
                ? columns.Select(IsSensitiveColumn).ToArray()
                : new bool[columns.Count];
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new string?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    row[i] = sensitive[i] ? "••••••" : DbValue(reader, i);
                }
                rows.Add(row);
            }
        }
        await transaction.RollbackAsync(ct);
        stopwatch.Stop();
        return new DbQueryResultDto(columns, rows, truncated, stopwatch.ElapsedMilliseconds);
    }

    private static string? DbValue(IDataRecord record, int ordinal)
    {
        if (record.IsDBNull(ordinal))
        {
            return null;
        }
        var value = Convert.ToString(record.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
        // Browsing a JSON payload or long message must not turn an admin request into a multi-megabyte response.
        return value.Length <= 4_000 ? value : $"{value[..4_000]}…";
    }

    private static bool IsSensitiveColumn(string name) => name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("config", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TableName();
}
