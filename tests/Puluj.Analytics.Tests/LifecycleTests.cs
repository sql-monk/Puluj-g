using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Puluj.Analytics.Lifecycle;
using Puluj.Analytics.Persistence;
using Puluj.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Puluj.Analytics.Tests;

/// <summary>
/// P15 (ADR-0013) L02/L03: the backfill rebuilds `analytics.message_lifecycle` from durable evidence — stage rows give timings, a
/// legacy raw (no extraction) is filed as `legacy` with `timings_available = false` and `completion_available = false`, an event-sourced
/// row is never overwritten, a second pass changes nothing; the reconciliation reports roots/posts/edits against `raw_messages` and fills a
/// late analysis; the report keeps its denominators apart and marks the unknown as unavailable.
/// </summary>
public sealed class LifecycleTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private ServiceProvider? _services;
    private int _source;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("PULUJ_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            try
            {
                _container = new PostgreSqlBuilder("postgis/postgis:17-3.5").WithDatabase("puluj_lifecycle_test").Build();
                await _container.StartAsync();
                cs = _container.GetConnectionString();
            }
            catch (Exception)
            {
                _container = null;
                return;
            }
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Puluj"] = cs,
            ["Analytics:ReportCacheSeconds"] = "0",
            ["Analytics:LifecycleStaleMinutes"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<PulujDbContext>(o => Puluj.Infrastructure.DependencyInjection.ConfigureDbContext(o, cs));
        services.AddPulujAnalytics(config);
        _services = services.BuildServiceProvider();
        await using (var db = await _services.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis");
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE analytics.message_lifecycle, processing.observations, processing.extractions, processing.stage_results, processing.deliveries, messaging.events, llm_requests, targets, raw_messages RESTART IDENTITY CASCADE");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sources WHERE code LIKE 'test_lifecycle_%'");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO sources (code, name, type, trust_level, priority, enabled) VALUES ('test_lifecycle_a', 'Lifecycle A', 2, 0.9, 10, true)");
            _source = await db.Sources.Where(s => s.Code == "test_lifecycle_a").Select(s => s.SourceId).SingleAsync();
        }
        await using (var adb = await Factory.CreateDbContextAsync())
        {
            await adb.Database.MigrateAsync();
            await adb.Database.ExecuteSqlRawAsync("DELETE FROM analytics.state");
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private IDbContextFactory<AnalyticsDbContext> Factory => _services!.GetRequiredService<IDbContextFactory<AnalyticsDbContext>>();
    private LifecycleBackfill Backfill => _services!.GetRequiredService<LifecycleBackfill>();
    private LifecycleReconciliation Reconciliation => _services!.GetRequiredService<LifecycleReconciliation>();
    private LifecycleReportService Reports => _services!.GetRequiredService<LifecycleReportService>();

    private async Task<long> RawAsync(string key, string revision, DateTimeOffset publishedAt, string? text, int status)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{revision}|{text}")));
        var rawPayload = text is null ? "{\"kind\":\"structured\"}" : "{}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, raw_payload, hash, processing_status, processed_at, attempts)
            VALUES ({_source}, {key + (revision == "0" ? "" : ":e" + revision)}, {key}, {revision}, {publishedAt}, {publishedAt.AddSeconds(30)}, {text}, {rawPayload}::jsonb, {hash}, {status}, {(status is 1 or 2 or 3 ? publishedAt.AddSeconds(60) : (DateTimeOffset?)null)}, 1)
            """);
        return (await db.Database.SqlQuery<long>($"SELECT max(raw_message_id) AS \"Value\" FROM raw_messages").ToListAsync()).Single();
    }

    /// <summary>Stage evidence of the platform path: an extraction with observations and stage_results rows in the given run.</summary>
    private async Task ExtractionAsync(long rawId, Guid runId, string outcome, string method, int facts, DateTimeOffset at)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var extraction = Guid.CreateVersion7();
        const string versions = "{\"rules\":\"rule-0.1\",\"ruleset_id\":\"v3\"}";
        var noIds = Array.Empty<Guid>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO processing.extractions (extraction_id, raw_message_id, run_id, extraction_version, method, outcome, versions, facts, llm_request_ids, finalized_by, created_at)
            VALUES ({extraction}, {rawId}, {runId}, 1, {method}, {outcome}, {versions}::jsonb, '[]'::jsonb, {noIds}, 'finalizer@test', {at})
            """);
        for (var i = 0; i < facts; i++)
        {
            var payload = i == 0 ? "{\"location\":{\"kind\":\"city\"}}" : "{\"location\":null}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO processing.observations (observation_id, extraction_id, raw_message_id, run_id, event_kind_code, category, effective_at, payload)
                VALUES ({Guid.CreateVersion7()}, {extraction}, {rawId}, {runId}, 'impact.explosion.reported', 'incident', {at}, {payload}::jsonb)
                """);
        }
        foreach (var (stage, offset) in new[] { ("normalize", 1), ("parse", 2), ("finalize", 3) })
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO processing.stage_results (raw_message_id, run_id, stage, stage_version, outcome, started_at, finished_at, worker)
                VALUES ({rawId}, {runId}, {stage}, '1', 'completed', {at.AddSeconds(offset - 1)}, {at.AddSeconds(offset)}, 'test')
                """);
        }
    }

    [Fact]
    public async Task L02_Backfill_from_evidence_legacy_unavailable_event_rows_kept_idempotent_and_reconciliation_counts()
    {
        if (_services is null)
        {
            return; // no database available
        }
        var run = Guid.CreateVersion7();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            const string runVersions = "{\"pipeline_version\":\"1.0-test\"}";
            await db.Database.ExecuteSqlAsync($"INSERT INTO processing.runs (run_id, lane, kind, state, versions, created_by, created_at, updated_at) VALUES ({run}, 'live', 'live', 'running', {runVersions}::jsonb, 'test', now(), now())");
        }
        // A platform-path raw (stage evidence), its edit (same post, new revision), a legacy raw (no extraction; processed by the old loop), a no-text structured raw.
        var a = await RawAsync("post-1", "0", t0, "Вибухи у Харкові.", 0);
        await ExtractionAsync(a, run, "completed", "rules", 2, t0.AddMinutes(1));
        var aEdit = await RawAsync("post-1", "1700000000", t0.AddMinutes(2), "Вибухи у Харкові (оновлено).", 0);
        await ExtractionAsync(aEdit, run, "completed", "rules", 1, t0.AddMinutes(3));
        var legacy = await RawAsync("post-2", "0", t0.AddMinutes(5), "Шахеди на Сумщині.", 1);
        var noText = await RawAsync("post-3", "0", t0.AddMinutes(6), null, 3);
        // An event-sourced row for the legacy raw's neighbour: the backfill must not overwrite it.
        var eventRow = await RawAsync("post-4", "0", t0.AddMinutes(7), "Пожежа у Полтаві.", 0);
        await ExtractionAsync(eventRow, run, "no_facts", "rules", 0, t0.AddMinutes(8));
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var noBranches = Array.Empty<string>();
            var noIds = Array.Empty<long>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO analytics.message_lifecycle (raw_message_id, run_id, source_id, source_message_key, source_revision, lane, published_at, received_at, stored_at, has_text, text_length, has_payload, is_edit,
                    analyzed_at, analysis_outcome, method, fact_count, unlocated_facts, timings_available, expected_branches, branches_done, domain_completed_at, completion_available, incident_ids, track_ids, alert_ids,
                    llm_calls, llm_input_tokens, llm_cache_tokens, llm_output_tokens, llm_cost_usd, llm_latency_ms, source_of_truth, updated_at)
                VALUES ({eventRow}, {run}, {_source}, 'post-4', '0', 'live', {t0.AddMinutes(7)}, {t0.AddMinutes(7).AddSeconds(30)}, {t0.AddMinutes(7).AddSeconds(31)}, true, 17, false, false,
                    {t0.AddMinutes(8)}, 'completed', 'llm', 3, 0, true, {noBranches}, {noBranches}, {t0.AddMinutes(9)}, true, {noIds}, {noIds}, {noIds}, 1, 100, 0, 20, 0.01, 800, 'event', now())
                """);
        }

        var progress = await Backfill.RunAsync(batchSize: 3, maxBatches: 10, CancellationToken.None);
        Assert.True(progress.Cursor >= eventRow, $"cursor {progress.Cursor}");
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var rows = await db.Lifecycle.AsNoTracking().OrderBy(r => r.RawMessageId).ToListAsync();
            Assert.Equal(5, rows.Count);
            var rowA = rows.Single(r => r.RawMessageId == a);
            Assert.Equal(run, rowA.RunId);
            Assert.Equal("completed", rowA.AnalysisOutcome);
            Assert.Equal("rules", rowA.Method);
            Assert.Equal(2, rowA.FactCount);
            Assert.Equal(1, rowA.UnlocatedFacts);
            Assert.True(rowA.TimingsAvailable);
            Assert.Contains("parse_at", rowA.Timings!);
            Assert.Equal("backfill", rowA.SourceOfTruth);
            Assert.False(rowA.IsEdit);
            Assert.Equal("post-1", rowA.SourceMessageKey);
            Assert.Equal(new[] { "post-1" }, rows.Where(r => r.RawMessageId == aEdit).Select(r => r.SourceMessageKey));
            Assert.True(rows.Single(r => r.RawMessageId == aEdit).IsEdit);
            var rowLegacy = rows.Single(r => r.RawMessageId == legacy);
            Assert.Equal(LifecycleBackfill.LegacyRun, rowLegacy.RunId);
            Assert.Equal("legacy", rowLegacy.Lane);
            Assert.Equal("legacy", rowLegacy.AnalysisOutcome);
            Assert.False(rowLegacy.TimingsAvailable); // never invented
            Assert.False(rowLegacy.CompletionAvailable);
            Assert.Null(rowLegacy.Timings);
            var rowNoText = rows.Single(r => r.RawMessageId == noText);
            Assert.False(rowNoText.HasText);
            Assert.True(rowNoText.HasPayload);
            Assert.Equal("unsupported", rowNoText.AnalysisOutcome); // processing_status Skipped
            var kept = rows.Single(r => r.RawMessageId == eventRow);
            Assert.Equal("event", kept.SourceOfTruth);
            Assert.Equal("llm", kept.Method); // the backfill saw `rules` in the extraction but the event row wins
            Assert.Equal(3, kept.FactCount);
            Assert.Equal(1, kept.LlmCalls);
        }
        // Idempotent: a second full pass changes nothing.
        await Backfill.ResetCursorAsync(CancellationToken.None);
        var again = await Backfill.RunAsync(batchSize: 100, maxBatches: 10, CancellationToken.None);
        Assert.Equal(progress.Cursor, again.Cursor);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            Assert.Equal(5, await db.Lifecycle.CountAsync());
            Assert.Equal("event", (await db.Lifecycle.SingleAsync(r => r.RawMessageId == eventRow)).SourceOfTruth);
        }

        // Reconciliation: denominators apart (raw rows 5, posts 4, edits 1), nothing missing; a late analysis (row without analysis, extraction present) is filled.
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE analytics.message_lifecycle SET analyzed_at = NULL, analysis_outcome = NULL, fact_count = 0 WHERE raw_message_id = {a}");
        }
        var report = await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(5, report.RawRows);
        Assert.Equal(4, report.Posts);
        Assert.Equal(1, report.Edits);
        Assert.Equal(5, report.ProjectedRaw);
        Assert.Equal(4, report.ProjectedPosts);
        Assert.Equal(0, report.MissingRoots);
        Assert.Equal(1, report.LateAnalyses);
        Assert.Equal(2, report.UnavailableTimings); // the legacy raw and the structured one (no stage rows)
        Assert.Equal(4, report.UnavailableCompletion); // + the two stage rows: no archived events in this synthetic run → receipts unknown, not «not completed»
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var late = await db.Lifecycle.AsNoTracking().SingleAsync(r => r.RawMessageId == a);
            Assert.Equal("completed", late.AnalysisOutcome);
            Assert.Equal(2, late.FactCount);
            Assert.Equal("reconciliation", late.SourceOfTruth);
        }
        // A raw the projection never saw (deleted row) is found as missing and refilled from evidence.
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM analytics.message_lifecycle WHERE raw_message_id = {noText}");
        }
        report = await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(1, report.MissingRoots);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            Assert.Equal(5, await db.Lifecycle.CountAsync());
        }
        Assert.Equal(0, (await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None)).MissingRoots);
    }

    /// <summary>An archived `observations.recorded` event of the run with one delivery row per domain branch (outcome NULL = still pending).</summary>
    private async Task<Guid> ObservationsEventAsync(long rawId, Guid runId, DateTimeOffset at, params (string Subscription, string? Outcome)[] deliveries)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var eventId = Guid.CreateVersion7();
        var envelope = "{\"event_type\":\"observations.recorded\"}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO messaging.events (event_id, event_type, lane, correlation_id, causation_id, raw_message_id, processing_run_id, topology_version, occurred_at, published_at, envelope, archived_at)
            VALUES ({eventId}, 'observations.recorded', 'live', {Guid.CreateVersion7()}, NULL, {rawId}, {runId}, 10, {at}, {at}, {envelope}::jsonb, {at})
            """);
        foreach (var (subscription, outcome) in deliveries)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, outcome, completed_at, lane, occurred_at)
                VALUES ({eventId}, {subscription}, 10, {at}, {outcome}, {(outcome is null ? (DateTimeOffset?)null : at.AddSeconds(5))}, 'live', {at})
                """);
        }
        return eventId;
    }

    [Fact]
    public async Task L04_Backfill_and_reconciliation_complete_domain_from_receipts_noop_counts_and_llm_cost_is_computed()
    {
        if (_services is null)
        {
            return; // no database available
        }
        var run = Guid.CreateVersion7();
        var t0 = new DateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero); // whole seconds: timestamps round-trip through timestamptz exactly
        await using (var db = await Factory.CreateDbContextAsync())
        {
            const string runVersions = "{\"pipeline_version\":\"1.0-test\"}";
            await db.Database.ExecuteSqlAsync($"INSERT INTO processing.runs (run_id, lane, kind, state, versions, created_by, created_at, updated_at) VALUES ({run}, 'live', 'live', 'running', {runVersions}::jsonb, 'test', now(), now())");
        }
        // p1: LLM analysis with two llm_requests rows (one failed with a 429, still a call) and both domain branches terminal (completed + noop).
        var p1 = await RawAsync("post-1", "0", t0, "Ракети над Полтавщиною.", 0);
        await ExtractionAsync(p1, run, "completed", "llm", 2, t0.AddMinutes(1));
        await using (var db = await Factory.CreateDbContextAsync())
        {
            foreach (var (outcome, status, input, cacheRead, cacheCreate, output, cost, ms) in new[] { ("completed", 200, 1200L, 800L, 0L, 150L, 0.0123m, 1400), ("429", 429, 0L, 0L, 0L, 0L, 0m, 300) })
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO llm_requests (raw_message_id, source_id, occurred_at, worker, model, prompt_version, outcome, status_code, duration_ms, input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens, estimated_cost_usd, facts_count, request_text, system_prompt, run_id)
                    VALUES ({p1}, {_source}, {t0.AddSeconds(40)}, 'parser@test', 'test-model', 'v1', {outcome}, {status}, {ms}, {input}, {cacheCreate}, {cacheRead}, {output}, {cost}, 2, 'req', 'sys', {run})
                    """);
            }
        }
        await ObservationsEventAsync(p1, run, t0.AddMinutes(1).AddSeconds(2), ("incident-worker", "completed"), ("track-worker", "noop"));
        // p2: one branch still pending at backfill time — completes later through the reconciliation (a late receipt, no event said so).
        var p2 = await RawAsync("post-2", "0", t0.AddMinutes(2), "Вибух у Сумах.", 0);
        await ExtractionAsync(p2, run, "completed", "rules", 1, t0.AddMinutes(3));
        var p2Event = await ObservationsEventAsync(p2, run, t0.AddMinutes(3).AddSeconds(2), ("incident-worker", null), ("track-worker", "completed"));
        // p3: no facts → no observations.recorded, but the run's events are archived (raw.stored) → nothing expected, complete at analysis.
        var p3 = await RawAsync("post-3", "0", t0.AddMinutes(4), "Тиша.", 0);
        await ExtractionAsync(p3, run, "no_facts", "rules", 0, t0.AddMinutes(5));
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var envelope = "{\"event_type\":\"raw.stored\"}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO messaging.events (event_id, event_type, lane, correlation_id, causation_id, raw_message_id, processing_run_id, topology_version, occurred_at, published_at, envelope, archived_at)
                VALUES ({Guid.CreateVersion7()}, 'raw.stored', 'live', {Guid.CreateVersion7()}, NULL, {p3}, {run}, 10, {t0.AddMinutes(4).AddSeconds(31)}, {t0.AddMinutes(4).AddSeconds(31)}, {envelope}::jsonb, {t0.AddMinutes(4).AddSeconds(32)})
                """);
        }
        // p4: stage evidence without any archived event (a run before the archive existed) → completion unknown, never «not completed».
        var p4 = await RawAsync("post-4", "0", t0.AddMinutes(6), "Дрони над Черніговом.", 0);
        await ExtractionAsync(p4, run, "completed", "rules", 1, t0.AddMinutes(7));

        var progress = await Backfill.RunAsync(batchSize: 100, maxBatches: 10, CancellationToken.None);
        Assert.Equal(4, progress.Processed);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var rows = await db.Lifecycle.AsNoTracking().OrderBy(r => r.RawMessageId).ToListAsync();
            var r1 = rows.Single(r => r.RawMessageId == p1);
            Assert.Equal(2, r1.LlmCalls); // the failed call counts: it cost latency and a request
            Assert.Equal(1200, r1.LlmInputTokens);
            Assert.Equal(800, r1.LlmCacheTokens);
            Assert.Equal(150, r1.LlmOutputTokens);
            Assert.Equal(0.0123m, r1.LlmCostUsd);
            Assert.Equal(1700, r1.LlmLatencyMs);
            Assert.Equal(["incident-worker", "track-worker"], r1.ExpectedBranches.Order());
            Assert.True(r1.CompletionAvailable);
            Assert.NotNull(r1.DomainCompletedAt); // noop is terminal: the branch looked and had nothing to write
            var r2 = rows.Single(r => r.RawMessageId == p2);
            Assert.True(r2.CompletionAvailable);
            Assert.Null(r2.DomainCompletedAt); // incident-worker still pending
            var r3 = rows.Single(r => r.RawMessageId == p3);
            Assert.Empty(r3.ExpectedBranches);
            Assert.True(r3.CompletionAvailable);
            Assert.Equal(t0.AddMinutes(5), r3.DomainCompletedAt);
            var r4 = rows.Single(r => r.RawMessageId == p4);
            Assert.False(r4.CompletionAvailable);
            Assert.Null(r4.DomainCompletedAt);
        }

        // Reconciliation before the receipt lands: p2 is pending (not late), nothing completes.
        var report = await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(0, report.LateCompletions);
        Assert.Equal(1, report.PendingDomain);
        Assert.Equal(1, report.UnavailableCompletion);
        // The receipt lands (a noop this time) → the next pass completes p2 from receipts and marks the row as reconciled.
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE processing.deliveries SET outcome = 'noop', completed_at = {t0.AddMinutes(20)} WHERE event_id = {p2Event} AND subscription_id = 'incident-worker'");
        }
        report = await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(1, report.LateCompletions);
        Assert.Equal(0, report.PendingDomain);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var r2 = await db.Lifecycle.AsNoTracking().SingleAsync(r => r.RawMessageId == p2);
            Assert.Equal(t0.AddMinutes(20), r2.DomainCompletedAt);
            Assert.Equal("reconciliation", r2.SourceOfTruth);
        }
        // A row whose expected set is empty (nothing to wait for) but which never got its completion — completes without any receipt (review B1: NULLs of the LEFT JOIN are not «pending»).
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE analytics.message_lifecycle SET domain_completed_at = NULL WHERE raw_message_id = {p3}");
        }
        report = await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(1, report.LateCompletions);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var r3 = await db.Lifecycle.AsNoTracking().SingleAsync(r => r.RawMessageId == p3);
            Assert.Equal(t0.AddMinutes(5), r3.DomainCompletedAt); // falls back to analyzed_at
            Assert.Equal(0, await db.Lifecycle.CountAsync(r => r.RawMessageId == p4 && r.DomainCompletedAt != null)); // unknown stays unknown
        }
        Assert.Equal(0, (await Reconciliation.RunAsync(TimeSpan.FromHours(24), TimeSpan.Zero, CancellationToken.None)).LateCompletions);
    }

    [Fact]
    public async Task L03_Report_keeps_denominators_apart_and_marks_unknown_as_unavailable()
    {
        if (_services is null)
        {
            return;
        }
        var run = Guid.CreateVersion7();
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var localYesterday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, kyiv).Date.AddDays(-1);
        var kyivMidnight = new DateTimeOffset(localYesterday, kyiv.GetUtcOffset(localYesterday)).ToUniversalTime(); // 00:00 Europe/Kyiv of yesterday, whatever the DST offset
        await using (var db = await Factory.CreateDbContextAsync())
        {
            const string runVersions = "{\"pipeline_version\":\"1.0-test\"}";
            await db.Database.ExecuteSqlAsync($"INSERT INTO processing.runs (run_id, lane, kind, state, versions, created_by, created_at, updated_at) VALUES ({run}, 'live', 'live', 'running', {runVersions}::jsonb, 'test', now(), now())");
        }
        var beforeMidnight = await RawAsync("d-1", "0", kyivMidnight.AddMinutes(-30), "Вибухи у Харкові.", 0);
        await ExtractionAsync(beforeMidnight, run, "completed", "rules", 1, kyivMidnight.AddMinutes(-29));
        var afterMidnight = await RawAsync("d-2", "0", kyivMidnight.AddMinutes(30), "Вибухи у Полтаві.", 0);
        await ExtractionAsync(afterMidnight, run, "completed", "rules", 2, kyivMidnight.AddMinutes(31));
        await RawAsync("d-2", "1700000001", kyivMidnight.AddMinutes(40), "Вибухи у Полтаві (оновлено).", 0); // edit: same post, no analysis yet
        await RawAsync("d-3", "0", kyivMidnight.AddMinutes(50), null, 3); // structured, unsupported (legacy status)
        await RawAsync("d-4", "0", kyivMidnight.AddMinutes(55), "Старе повідомлення.", 1); // legacy processed: timings unavailable
        await Backfill.RunAsync(100, 10, CancellationToken.None);

        var report = await Reports.ReportAsync(168, CancellationToken.None);
        Assert.NotNull(report);
        Assert.Equal("day", report.Bucket);
        Assert.Equal(5, report.Funnel.Raw);
        Assert.Equal(4, report.Funnel.Posts); // the edit is the same post
        Assert.Equal(4, report.Funnel.Analyzed); // 2 stage analyses + 2 legacy outcomes (unsupported, legacy); the edit has none
        Assert.Equal(2, report.Funnel.WithFacts);
        Assert.Equal(3, report.Funnel.UnavailableTimings); // the two legacy rows and the edit without an analysis (no stage rows)
        Assert.Equal(5, report.Funnel.UnavailableCompletion); // no archived events in the synthetic run
        Assert.Equal(1, report.Funnel.StuckAnalysis); // the edit: no analysis, older than the (zero) stale window
        var source = Assert.Single(report.Sources);
        Assert.Equal(5, source.Raw);
        Assert.Equal(4, source.Posts);
        Assert.Equal(1, source.Edits);
        Assert.Equal(1, source.NoText);
        Assert.Equal(3, source.Facts);
        Assert.Equal(30d, source.CollectDelayP50Seconds);
        Assert.Equal(2, report.Parse.Outcomes["completed"]);
        Assert.Equal(1, report.Parse.Outcomes["unsupported"]);
        Assert.Equal(1, report.Parse.Outcomes["legacy"]);
        Assert.Equal(1, report.Parse.Outcomes["pending"]);
        Assert.Equal(1, report.Parse.MultiFact);
        Assert.Equal(1, report.Parse.Unlocated);
        Assert.Equal(2, report.Parse.RuleVersions["v3"]);
        Assert.StartsWith("unavailable", report.Quality.PrecisionRecall);
        Assert.Equal(0, report.Cost.Calls);
        // Kyiv-day buckets: the two analysed raws straddle the local midnight and land in two different days.
        var days = report.Timeline.Select(b => b.At).Distinct().ToList();
        Assert.Contains(kyivMidnight, days);
        Assert.Contains(kyivMidnight.AddDays(-1), days);
        Assert.Equal(1, report.Timeline.Single(b => b.At == kyivMidnight.AddDays(-1)).Raw);
        Assert.Equal(4, report.Timeline.Single(b => b.At == kyivMidnight).Raw);
        var run24 = await Reports.ReportAsync(24, CancellationToken.None);
        Assert.Equal("hour", run24!.Bucket);
        Assert.Null(await Reports.ReportAsync(100, CancellationToken.None));
        Assert.Contains(report.History.Runs, r => r.RunId == run && r.PipelineVersion == "1.0-test" && r.Outcomes["completed"] == 2);
        Assert.Contains(report.History.Runs, r => r.RunId == LifecycleBackfill.LegacyRun && r.Kind == "legacy");
    }
}
