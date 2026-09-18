using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Integration.Tests;

public sealed class P00BaselineAttribute : FactAttribute
{
    public P00BaselineAttribute()
    {
        if (Environment.GetEnvironmentVariable("PULUJ_RUN_BASELINE") != "1")
            Skip = "Opt-in load profile: set PULUJ_RUN_BASELINE=1 and PULUJ_EVIDENCE_DIRECTORY; requires expendable PostGIS.";
    }
}

/// <summary>Fixed synthetic inputs; real claims, processor, sinks, triggers, commits. No live/provider traffic.</summary>
[Collection(PipelineCollection.Name)]
public sealed class P00BaselineTests(PipelineFixture fixture)
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private const int Count = 120;

    [P00Baseline]
    public async Task Record_committed_baseline_for_one_two_four_workers()
    {
        var directory = Environment.GetEnvironmentVariable("PULUJ_EVIDENCE_DIRECTORY")
            ?? throw new InvalidOperationException("PULUJ_EVIDENCE_DIRECTORY required");
        Directory.CreateDirectory(directory);
        await Inventory(directory);
        var rows = new List<object>();
        // Warm EF, parser and JIT with an unreported mixed run. Each measured run resets the same DB.
        await Run("mixed", 1, 0);
        foreach (var profile in new[] { "mixed", "single-category", "alerts", "no-facts", "slow-parser-stub", "live-only", "history-live" })
        foreach (var workers in new[] { 1, 2, 4 })
        for (var repeat = 1; repeat <= 2; repeat++)
        {
            rows.Add(await Run(profile, workers, repeat));
            // Persist after each completed sample, so interruption never produces fabricated completeness.
            await File.WriteAllTextAsync(Path.Combine(directory, "P00-baseline.json"), JsonSerializer.Serialize(new
            {
                recordedAt = DateTimeOffset.UtcNow, schema = 1,
                assemblySha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(RawMessageProcessor).Assembly.Location))),
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                logicalProcessors = Environment.ProcessorCount, inputVersion = "p00-synthetic-v1", messagesPerRun = Count,
                notes = new[] { "Workers are concurrent claim/processor loops with distinct identities in one testhost, sharing a DB/pool; not separate OS processes.",
                    "Parser is real rules except explicit 25ms slow-parser stub. LLM provider is absent.",
                    "Root latency includes claim+process+postcommit notify; backlog latency begins at drain start (preloaded backlog).",
                    "Stage summaries expose existing p50/p90/max, not p95/p99. Root percentiles below are measured directly.",
                    "SQL lock samples are pg_stat_activity sampled every 20ms, not exact wait time. No separate pool/commit timing or DB CPU/disk attribution.",
                    "Host is shared with existing services; no throughput guarantee or production SLO is inferred." },
                runs = rows
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task<object> Run(string profile, int workers, int repeat)
    {
        await using (var db = await Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("TRUNCATE raw_messages, targets, target_tracks, air_alerts RESTART IDENTITY CASCADE");
        var ingestor = Services.GetRequiredService<RawMessageIngestor>();
        await using var setup = await Factory.CreateDbContextAsync();
        var sources = await setup.Sources.ToDictionaryAsync(s => s.Code, s => s.SourceId);
        var live = new HashSet<long>();
        var inputs = new List<string>();
        for (var i = 0; i < Count; i++)
        {
            // Identical 30 live inputs in both profiles; replay adds only the other 90 historical roots.
            if (profile == "live-only" && i % 4 != 0) continue;
            var isAlert = profile == "alerts" || profile == "mixed" && i % 4 == 2;
            var noFacts = profile == "no-facts" || profile == "mixed" && i % 4 == 3;
            var text = noFacts ? "Дякуємо за увагу." : profile == "mixed" && i % 4 == 1
                ? "Ракета на Сумщині курсом на Полтавщину." : "Шахеди на Сумщині курсом на Полтавщину.";
            var code = isAlert ? "alerts_in_ua" : i % 2 == 0 ? "tg_kpszsu" : "tg_monitoringwar";
            // Static dates make input identical across worker counts. The two lanes differ only in event time.
            var isLive = profile == "live-only" || profile == "history-live" && i % 4 == 0;
            var at = (isLive ? At.AddDays(4) : At).AddMinutes(i * 4);
            var key = $"baseline-{i}";
            var payload = isAlert ? JsonSerializer.SerializeToDocument(new
            {
                kind = "alert.started", at, test = key,
                alert = new { id = key, location_title = "Сумська область", location_oblast = "Сумська область", location_type = "oblast", alert_type = "air_raid", started_at = at, finished_at = at.AddMinutes(2) }
            }) : JsonSerializer.SerializeToDocument(new { test = key });
            inputs.Add($"{code}|{at:O}|{text}|{payload.RootElement.GetRawText()}");
            var result = await ingestor.IngestAsync(new()
            {
                SourceId = sources[code], SourceMessageId = key, PublishedAt = at,
                RawText = isAlert ? null : text, RawPayload = payload
            }, code, CancellationToken.None, announceProcessor: false);
            Assert.True(result.IsNew);
            if (isLive) live.Add(result.RawMessageId!.Value);
        }
        var inputHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', inputs))));
        var expected = inputs.Count;
        var stats = new ProcessingStats(TimeProvider.System);
        IParser parser = Services.GetRequiredService<RuleParser>();
        if (profile == "slow-parser-stub") parser = new SlowParser(parser);
        var claims = Services.GetRequiredService<RawMessageClaims>();
        var attempts = new ConcurrentBag<(long Id, double Ms, double EndMs)>();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var waits = new ConcurrentDictionary<string, int>();
        var sample = SampleWaits(waits, sampling.Token);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, workers).Select(async worker =>
            {
                var name = $"baseline-{worker}";
                var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity(name), parser, stats);
                while (true)
                {
                    var start = timer.Elapsed.TotalMilliseconds;
                    var id = await claims.ClaimOldestAsync(name, timeout.Token);
                    if (id is null) break;
                    await processor.ProcessAsync(id.Value, timeout.Token);
                    attempts.Add((id.Value, timer.Elapsed.TotalMilliseconds - start, timer.Elapsed.TotalMilliseconds));
                }
            }));
        }
        finally
        {
            timer.Stop();
            await sampling.CancelAsync();
            await sample;
        }
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        process.Refresh();
        await using var check = await Factory.CreateDbContextAsync();
        var outcomes = await check.RawMessages.AsNoTracking().ToListAsync();
        var errors = await check.ProcessingErrors.CountAsync();
        Assert.Equal(expected, outcomes.Count);
        Assert.All(outcomes, r => Assert.Equal(ProcessingStatus.Processed, r.ProcessingStatus));
        Assert.Equal(0, errors);
        var snapshot = stats.Snapshot(workers);
        Assert.Equal(expected, snapshot.Processed);
        var values = attempts.Select(a => a.Ms).Order().ToArray();
        var liveValues = attempts.Where(a => live.Contains(a.Id)).Select(a => a.EndMs).Order().ToArray();
        var counts = await check.Targets.GroupBy(t => t.EventType).Select(g => new { type = g.Key.ToString(), count = g.Count() }).ToListAsync();
        var targetCount = await check.Targets.CountAsync();
        var trackCount = await check.TargetTracks.CountAsync();
        var alertCount = await check.AirAlerts.CountAsync();
        Assert.Equal(profile == "no-facts" ? 0 : profile == "mixed" ? 90 : expected, targetCount);
        Assert.Equal(profile == "alerts" ? expected : profile == "mixed" ? 30 : 0, alertCount);
        if (profile is "no-facts" or "alerts") Assert.Equal(0, trackCount);
        else Assert.True(trackCount > 0);
        if (profile == "mixed")
        {
            Assert.Equal(60, await check.Targets.CountAsync(t => t.EventType == EventType.TargetObserved));
            Assert.Equal(30, await check.Targets.CountAsync(t => t.EventType == EventType.AirRaidAlert));
            Assert.Equal(2, await check.Targets.Where(t => t.EventType == EventType.TargetObserved).Select(t => t.TargetCategoryId).Distinct().CountAsync());
        }
        return new
        {
            profile, workers, repeat, inputHash, completedRoots = outcomes.Count, attempts = attempts.Count,
            errors, retries = attempts.Count - outcomes.Count, drainMs = timer.Elapsed.TotalMilliseconds,
            rootsPerSecond = outcomes.Count / timer.Elapsed.TotalSeconds,
            rootP95Ms = Percentile(values, .95), rootP99Ms = Percentile(values, .99),
            liveRoots = live.Count, liveBacklogP95Ms = Percentile(liveValues, .95), liveBacklogP99Ms = Percentile(liveValues, .99),
            testhostCpuMs = cpuMs, testhostWorkingSetBytes = process.WorkingSet64,
            sqlWaitSamples = waits, stages = new { snapshot.Parse, snapshot.Lock, snapshot.Store, snapshot.Total },
            facts = counts, tracks = trackCount, alerts = alertCount
        };
    }

    private async Task SampleWaits(ConcurrentDictionary<string, int> waits, CancellationToken ct)
    {
        try
        {
            await using var db = await Factory.CreateDbContextAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var samples = await db.Database.SqlQueryRaw<string>("SELECT coalesce(wait_event_type, 'CPU/runnable') || ':' || coalesce(wait_event, 'active') AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state = 'active'").ToListAsync(ct);
                foreach (var wait in samples) waits.AddOrUpdate(wait, 1, (_, n) => n + 1);
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task Inventory(string directory)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        var queries = new Dictionary<string, string>
        {
            ["environment"] = "SELECT version(), postgis_full_version(), current_database(), current_setting('max_connections'), current_setting('shared_buffers')",
            ["migrations"] = "SELECT * FROM \"__EFMigrationsHistory\" ORDER BY 1",
            ["triggers"] = "SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE NOT t.tgisinternal AND n.nspname='public' ORDER BY 2,3",
            ["functions"] = "SELECT p.proname, pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proname LIKE 'puluj_%' ORDER BY 1",
            ["indexes"] = "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname='public' AND tablename <> 'spatial_ref_sys' ORDER BY 1,2",
            ["constraints"] = "SELECT c.relname, con.conname, pg_get_constraintdef(con.oid) FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname <> 'spatial_ref_sys' ORDER BY 1,2"
        };
        var inventory = new Dictionary<string, object>();
        foreach (var (name, sql) in queries)
        {
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<string[]>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString() ?? "").ToArray());
            inventory[name] = new { query = sql, rows };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "P00-sql-inventory.json"), JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double? Percentile(double[] sorted, double p) => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
    private sealed class SlowParser(IParser inner) : IParser
    {
        public async Task<IReadOnlyList<ParsedFact>> ParseAsync(NormalizedMessage message, ParseContext context, CancellationToken ct)
        {
            await Task.Delay(25, ct);
            return await inner.ParseAsync(message, context, ct);
        }
    }
}
