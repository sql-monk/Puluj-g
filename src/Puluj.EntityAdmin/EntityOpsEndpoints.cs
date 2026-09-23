using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Puluj.Admin;
using Puluj.Admin.Endpoints;
using Puluj.Analytics.Reporting;
using Puluj.EntityAdmin.Docker;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.EntityAdmin;

/// <summary>
/// Operations view of the system for the admin panel: which service is alive, what every instance reports about
/// itself, what each collector does, how the pipeline keeps up, the containers of the compose stack, what the database
/// holds, and the tail of every service's log. Statistics come from SQL, statuses from `app_settings`, containers from
/// the docker CLI (<see cref="DockerService"/>); the only writes are the container actions and the reprocess request.
/// </summary>
public static partial class EntityOpsEndpoints
{
    private static readonly TimeSpan WorkerStale = TimeSpan.FromSeconds(90);
    private static readonly string[] OperationalSchemas = ["public", "analytics"];
    private static readonly HashSet<string> ResetExcludedTables = new(StringComparer.Ordinal)
    {
        "__EFMigrationsHistory", "spatial_ref_sys", "app_settings", "sources", "places",
        "ee_extractors", "ee_entity_definitions",
        "target_categories", "target_classes", "target_families", "target_models", "target_model_aliases",
        "event_kinds", "event_kind_rulesets", "event_kind_rules", "event_kind_ruleset_audit", "event_kind_audit",
    };

    private sealed record TelegramChannelInfo(int SourceId, string? ChannelTitle, int? SubscriberCount);

    private static async Task<IReadOnlyDictionary<int, TelegramChannelInfo>> LatestTelegramInfoAsync(PulujDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<TelegramChannelInfo>($"""
            SELECT s.source_id AS source_id,
                   r.raw_payload ->> 'channelTitle' AS channel_title,
                   NULLIF(r.raw_payload ->> 'subscriberCount', '')::int AS subscriber_count
            FROM sources s
            CROSS JOIN LATERAL (
                SELECT r.raw_payload
                FROM raw_messages r
                WHERE r.source_id = s.source_id
                  AND r.raw_payload IS NOT NULL
                  AND (r.raw_payload ? 'channelTitle' OR r.raw_payload ? 'subscriberCount')
                ORDER BY r.received_at DESC, r.raw_message_id DESC
                LIMIT 1) r
            WHERE s.type = {(int)SourceType.Telegram}
            """).ToListAsync(ct);
        return rows.ToDictionary(x => x.SourceId);
    }

    public static IEndpointRouteBuilder MapEntityOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/admin").AddEndpointFilter(EntityAdminEndpoints.AuthorizeAsync);

        ops.MapGet("/ops/overview", OverviewAsync);
        ops.MapGet("/ops/workers", WorkersAsync);
        ops.MapGet("/ops/collectors", CollectorsAsync);
        ops.MapGet("/ops/pipeline", PipelineAsync);
        ops.MapGet("/ops/llm", LlmAsync);
        ops.MapGet("/ops/llm/requests", LlmRequestsAsync);
        ops.MapGet("/ops/llm/requests/{id:long}", LlmRequestAsync);
        ops.MapGet("/ops/messages", MessagesAsync);
        ops.MapGet("/ops/db", DbAsync);
        ops.MapGet("/ops/db/tables/{name}/rows", DbTableRowsAsync);
        ops.MapPost("/ops/db/tables/{name}/analyze", AnalyzeTableAsync);
        ops.MapPost("/ops/db/tables/{name}/vacuum", VacuumTableAsync);
        ops.MapPost("/ops/db/tables/{name}/reindex", ReindexTableAsync);
        ops.MapPost("/ops/db/query", DbQueryAsync);
        ops.MapPost("/ops/db/clear", ClearOperationalDataAsync);

        // Containers of the compose stack: list, restart / stop / start. Processor count is intentionally fixed at one.
        ops.MapGet("/ops/containers", async (DockerService docker, CancellationToken ct) => Results.Ok(await docker.ListAsync(ct)));
        ops.MapPost("/ops/containers/{id}/{action:regex(^(restart|stop|start)$)}", async (string id, string action, HttpContext http, DockerService docker, CancellationToken ct) =>
        {
            var outcome = await docker.ActAsync(id, action, http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Json(outcome.Result, statusCode: outcome.StatusCode);
        });
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
            // A history load owns the pause; a pause left by an earlier reprocess (in progress or failed) is ours to run over.
            if (await reprocess.PausedAsync(ct) is { } paused && !ReprocessService.OwnsPause(paused))
            {
                return Results.Conflict(new { error = $"Обробку призупинено: {paused}" });
            }
            var pending = await reprocess.ResetAsync(ct);
            // The analytics schema is another derived view of raw_messages. It can be absent before its worker first starts.
            var analyticsStatus = await analytics.StatusAsync(ct);
            if (analyticsStatus.Initialized)
            {
                await analytics.ResetAsync(ct);
            }
            return Results.Ok(new { pending, analyticsReset = analyticsStatus.Initialized });
        });

        // Testing without real sources: inject a message through the same direct database path as the collectors.
        ops.MapPost("/dev/ingest", async (IngestRequest req, ReferenceCache refs, RawMessageIngestor ingestor, TimeProvider clock, CancellationToken ct) =>
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
            var result = await ingestor.IngestAsync(message, source.Code, ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static async Task<IResult> ClearOperationalDataAsync(DbClearRequest request, HttpContext http, DockerService docker, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!string.Equals(request.Confirmation, "DELETE_ALL_OPERATIONAL_DATA", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "Для цієї операції потрібне точне підтвердження." });
        }

        var paused = await docker.PauseDataWritersAsync(http.Connection.RemoteIpAddress?.ToString(), ct);
        if (!paused.Ok)
        {
            return Results.Json(new { error = paused.Message }, statusCode: paused.StatusCode);
        }

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Database.OpenConnectionAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('puluj:clear-operational-data'))", ct);

            var connection = db.Database.GetDbConnection();
            await using var discover = connection.CreateCommand();
            discover.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            discover.CommandText = "SELECT schemaname, tablename FROM pg_tables WHERE schemaname = ANY (@schemas) ORDER BY schemaname, tablename";
            var schemas = discover.CreateParameter();
            schemas.ParameterName = "schemas";
            schemas.Value = OperationalSchemas;
            discover.Parameters.Add(schemas);

            var tables = new List<(string Schema, string Name)>();
            await using (var reader = await discover.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    if (!ResetExcludedTables.Contains(name)) tables.Add((schema, name));
                }
            }

            if (tables.Count > 0)
            {
                var names = string.Join(", ", tables.Select(t => $"{QuoteIdentifier(t.Schema)}.{QuoteIdentifier(t.Name)}"));
                await using var truncate = connection.CreateCommand();
                truncate.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                // `RESTART IDENTITY` requires ownership of every affected sequence. The admin service is intentionally
                // a non-owner role, so preserve monotonic IDs while clearing the data it is authorized to reset.
                truncate.CommandText = $"TRUNCATE TABLE {names} CASCADE";
                await truncate.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { tables = tables.Count, stoppedContainers = paused.Containers.Count });
        }
        catch
        {
            await docker.ResumeDataWritersAsync(paused.Containers, http.Connection.RemoteIpAddress?.ToString(), ct);
            throw;
        }
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
            Failures = g.LongCount(x => !AdminReadQueries.LlmOkOutcomes.Contains(x.Outcome)),
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

    private static async Task<IResult> LlmRequestsAsync(int? hours, string? outcome, string? q, long? beforeId, int? limit, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct)
    {
        var span = hours ?? 168;
        if (!PipelineBuckets.AllowedHours.Contains(span))
        {
            return Results.BadRequest(new { error = "Період має бути 24, 168 або 720 годин." });
        }
        if (outcome is not (null or "" or "all" or "failures" or "facts" or "empty" or "refusal" or "success"))
        {
            return Results.BadRequest(new { error = "Невідомий фільтр результату." });
        }
        var to = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        return Results.Ok(await AdminReadQueries.LlmRequestsAsync(db, to - TimeSpan.FromHours(span), to, outcome, q, beforeId, limit ?? 100, ct));
    }

    /// <summary>Messages of one source (the collectors' and sources' "view messages" link), or one message by id.</summary>
    private static async Task<IResult> MessagesAsync(int? sourceId, long? rawMessageId, string? cursor, int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (sourceId is null && rawMessageId is null)
        {
            return Results.BadRequest(new { error = "Виберіть джерело або вкажіть ID повідомлення." });
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        return Results.Ok(await AdminReadQueries.MessagesAsync(db, sourceId, rawMessageId, cursor, limit ?? 50, ct));
    }

    private static async Task<IResult> LlmRequestAsync(long id, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var x = await db.LlmRequests.AsNoTracking().Where(x => x.LlmRequestId == id).Select(x => new
        {
            x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, SourceCode = x.Source!.Code, x.Worker, x.Model, x.PromptVersion,
            x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens,
            x.EstimatedCostUsd, x.FactsCount, x.Error, x.RequestText, x.SystemPrompt, x.ResponseText,
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
        SettingsStore settings, EntityAdminStore entityStore, IDbContextFactory<PulujDbContext> factory, IHttpClientFactory http, IConfiguration config, TimeProvider clock, CancellationToken ct)
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

        // Entity Extractor: its health endpoint plus what its delivery queue says (a 200 alone does not mean it processes).
        var probe = await EntityAdminEndpoints.ProbeAsync(http, ct);
        var queue = await entityStore.QueueSnapshotAsync(ct);
        services.Add(EntityExtractorStatus.Describe(probe, queue, EntityAdminEndpoints.LlmEnabled(all, config), now));

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
    public static List<(string Name, DateTimeOffset At)> WorkerHeartbeats(IReadOnlyDictionary<string, AppSetting> all, DateTimeOffset now) =>
        WorkerStatusDocuments.Heartbeats(all, now);

    /// <summary>The instance's own status document (`Runtime:Worker:{name}:Status`, §2.1); null when absent or unreadable.</summary>
    public static WorkerStatusDto? WorkerStatus(IReadOnlyDictionary<string, AppSetting> all, string name) =>
        WorkerStatusDocuments.Status(all, name);

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
        var telegram = await LatestTelegramInfoAsync(db, ct);
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
            var channel = s.Config is not null && s.Config.RootElement.TryGetProperty("channel", out var configuredChannel) && configuredChannel.ValueKind == JsonValueKind.String
                ? configuredChannel.GetString()
                : null;
            return new CollectorStatusDto(s.SourceId, s.Code, s.Name, s.Type.ToString(), s.Enabled,
                st?.LastPolledAt, st?.LastSuccessAt, st?.LastMessageAt, st?.LastError, st?.ConsecutiveFailures ?? 0,
                perHour.Sum(), perHour, telegram.GetValueOrDefault(s.SourceId)?.ChannelTitle, telegram.GetValueOrDefault(s.SourceId)?.SubscriberCount, channel);
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

    /// <summary>A bounded OFFSET page ordered by primary key. Concurrent mutations can still shift rows between pages.</summary>
    private static async Task<IResult> DbTableRowsAsync(string name, int? limit, int? offset, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!TableName().IsMatch(name))
        {
            return Results.BadRequest(new { error = "Некоректна назва таблиці." });
        }
        var take = Math.Clamp(limit ?? 50, 1, 200);
        if (offset is < 0 or > MaxTableOffset)
            return Results.BadRequest(new { error = $"Зсув має бути від 0 до {MaxTableOffset}. Для глибшого пошуку використайте SQL-консоль." });
        var skip = offset ?? 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Database.SqlQueryRaw<string>("""
            SELECT relname AS "Value" FROM pg_stat_user_tables
            WHERE schemaname = 'public' AND relname = {0}
            """, name).AnyAsync(ct);
        if (!exists)
        {
            return Results.NotFound(new { error = "Таблицю не знайдено." });
        }
        var keys = await db.Database.SqlQueryRaw<string>("""
            SELECT a.attname::text AS "Value"
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = format('public.%I', {0})::regclass AND i.indisprimary
            ORDER BY array_position(i.indkey::int2[], a.attnum)
            """, name).ToListAsync(ct);
        var order = keys.Count > 0 ? $" ORDER BY {string.Join(", ", keys.Select(QuoteIdentifier))}" : string.Empty;
        try
        {
            var page = await ReadQueryAsync(db, $"SELECT * FROM public.{QuoteIdentifier(name)}{order} LIMIT {take + 1} OFFSET {skip}", take, redactSensitiveColumns: true, ct);
            return Results.Ok(page with { Offset = skip, OrderBy = keys.Count > 0 ? keys : null });
        }
        catch (Exception e) when (DatabaseQueryErrors.Describe(e) is { } error)
        {
            return Results.BadRequest(error);
        }
    }

    private const int MaxTableOffset = 1_000_000;

    private static Task<IResult> AnalyzeTableAsync(string name, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
        => MaintainTableAsync(name, TableMaintenance.Analyze, factory, ct);

    private static Task<IResult> VacuumTableAsync(string name, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
        => MaintainTableAsync(name, TableMaintenance.Vacuum, factory, ct);

    private static Task<IResult> ReindexTableAsync(string name, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
        => MaintainTableAsync(name, TableMaintenance.Reindex, factory, ct);

    private static async Task<IResult> MaintainTableAsync(string name, TableMaintenance maintenance, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!TableName().IsMatch(name))
        {
            return Results.BadRequest(new { error = "Некоректна назва таблиці." });
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Database.SqlQueryRaw<string>("""
            SELECT relname AS "Value" FROM pg_stat_user_tables
            WHERE schemaname = 'public' AND relname = {0}
            """, name).AnyAsync(ct);
        if (!exists)
        {
            return Results.NotFound(new { error = "Таблицю не знайдено." });
        }

        var stopwatch = Stopwatch.StartNew();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        var table = $"public.{QuoteIdentifier(name)}";
        var (operation, sql, timeoutSeconds) = maintenance switch
        {
            TableMaintenance.Analyze => ("ANALYZE", $"ANALYZE {table}", 60),
            TableMaintenance.Vacuum => ("VACUUM", $"VACUUM {table}", 120),
            // Concurrent reindexing keeps the table available for ordinary reads and writes.
            TableMaintenance.Reindex => ("REINDEX", $"REINDEX TABLE CONCURRENTLY {table}", 300),
            _ => throw new ArgumentOutOfRangeException(nameof(maintenance)),
        };
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
        stopwatch.Stop();
        return Results.Ok(new DbTableMaintenanceResultDto(name, operation, stopwatch.ElapsedMilliseconds));
    }

    private static async Task<IResult> DbQueryAsync(DbQueryRequest request, IDbContextFactory<PulujDbContext> factory, CancellationToken ct)
    {
        if (!DatabaseQueryGuard.TryValidate(request.Sql, out var error))
        {
            return Results.BadRequest(new { error });
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        try
        {
            return Results.Ok(await ReadQueryAsync(db, request.Sql, 200, redactSensitiveColumns: false, ct));
        }
        catch (Exception e) when (DatabaseQueryErrors.Describe(e) is { } queryError)
        {
            // The operator's own SQL is wrong (syntax, unknown column, timeout): say why and where, as a 400, not a 500.
            return Results.BadRequest(queryError);
        }
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

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static bool IsSensitiveColumn(string name) => name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("config", StringComparison.OrdinalIgnoreCase);

    private enum TableMaintenance { Analyze, Vacuum, Reindex }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TableName();
}
