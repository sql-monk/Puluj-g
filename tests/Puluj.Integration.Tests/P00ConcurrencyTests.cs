using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Correlation;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Integration.Tests;

/// <summary>Real processor/sinks/triggers on PostGIS. Each scenario starts with an empty derived/input dataset.</summary>
[Collection(PipelineCollection.Name)]
public sealed class P00ConcurrencyTests(PipelineFixture fixture) : IAsyncLifetime
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
    }
    public Task DisposeAsync() => InitializeAsync(); // Do not leak this class's synthetic data into legacy tests.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_start_end_concurrent_preserves_one_interval_and_both_messages(bool endFirst)
    {
        var start = await Structured("start", "same-alert", false);
        var end = await Structured("end", "same-alert", true);
        var ordered = endFirst ? new[] { end, start } : new[] { start, end };
        var tasks = new List<Task<int>>();
        await using (var held = await Factory.CreateDbContextAsync())
        await using (var tx = await held.Database.BeginTransactionAsync())
        {
            await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
            foreach (var id in ordered)
            {
                tasks.Add(Processor().ProcessAsync(id, CancellationToken.None));
                await WaitForStoreWaiters(tasks.Count);
            }
            // Both real transactions are parked before derived reads; release them in the established order.
            await tx.CommitAsync();
        }
        Assert.All(await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)), n => Assert.Equal(1, n));
        await using var db = await Factory.CreateDbContextAsync();
        var alert = await db.AirAlerts.SingleAsync();
        Assert.Equal(At, alert.StartedAt);
        Assert.Equal(At.AddMinutes(10), alert.EndedAt);
        Assert.Equal(start, alert.StartRawMessageId);
        Assert.Equal(end, alert.EndRawMessageId);
        await AssertCommitted(db, [start, end]);
    }

    [Fact]
    public async Task Concurrent_duplicate_starts_do_not_retry_or_duplicate_interval()
    {
        var ids = new List<long>();
        for (var i = 0; i < 12; i++) ids.Add(await Structured($"start-{i}", "duplicate-alert", false));
        await Task.WhenAll(ids.Select(id => Processor().ProcessAsync(id, CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(30));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.AirAlerts.CountAsync());
        await AssertCommitted(db, ids);
    }

    [Fact]
    public async Task Concurrent_observations_create_one_track_with_all_evidence()
    {
        var ids = new List<long>();
        for (var i = 0; i < 12; i++) ids.Add(await Text($"same-{i}", "Шахеди на Сумщині курсом на Полтавщину.", At, i % 2 == 0 ? "tg_kpszsu" : "tg_monitoringwar"));
        await Task.WhenAll(ids.Select(id => Processor().ProcessAsync(id, CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(45));
        await using var db = await Factory.CreateDbContextAsync();
        var track = await db.TargetTracks.SingleAsync();
        Assert.Equal(1, track.TargetCount); // Canonical observations, while all 12 evidence links are retained.
        Assert.Equal(2, track.DistinctSourceCount);
        Assert.Equal(ids.Count, await db.TrackTargets.CountAsync());
        Assert.Equal(ids.Count, await db.TargetTrackRevisions.CountAsync());
        Assert.Equal(ids.Count - 1, await db.Targets.CountAsync(t => t.DuplicateOfTargetId != null));
        await AssertCommitted(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_cancellation_covers_grandchildren_even_when_end_is_processed_first(bool endFirst)
    {
        // Choose a real seeded grandchild, independent of optional national gazetteer file versions.
        await using var db = await Factory.CreateDbContextAsync();
        var nested = await (from child in db.Places
                            join parent in db.Places on child.ParentId equals parent.PlaceId
                            join root in db.Places on parent.ParentId equals root.PlaceId
                            where root.Level == PlaceLevel.Region
                            select new { Child = child.PlaceId, Root = root.PlaceId }).FirstAsync();
        var gaz = Services.GetRequiredService<Puluj.Processing.Indexes.IIndexes>().Gazetteer;
        ParsedFact Fact(int place, EventType type) => new()
        {
            SegmentIndex = 0, SegmentText = "synthetic alert fact", EventType = type,
            Places = [new(gaz.Get(place)!, PlaceRole.Current, "synthetic", 0, 1, 100)]
        };
        var start = await Text("nested-start", "synthetic start", At);
        var end = await Text("nested-end", "synthetic end", At.AddMinutes(10));
        var startProcessor = Processor(new FixedParser(Fact(nested.Child, EventType.AirRaidAlert)));
        var endProcessor = Processor(new FixedParser(Fact(nested.Root, EventType.AlertCancelled)));
        var work = endFirst
            ? new[] { (endProcessor, end), (startProcessor, start) }
            : new[] { (startProcessor, start), (endProcessor, end) };
        var tasks = new List<Task<int>>();
        await using (var held = await Factory.CreateDbContextAsync())
        await using (var tx = await held.Database.BeginTransactionAsync())
        {
            await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
            foreach (var (processor, id) in work)
            {
                tasks.Add(processor.ProcessAsync(id, CancellationToken.None));
                await WaitForStoreWaiters(tasks.Count);
            }
            await tx.CommitAsync();
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        var interval = await db.AirAlerts.SingleAsync();
        Assert.Equal(At.AddMinutes(10), interval.EndedAt);
        Assert.Equal(start, interval.StartRawMessageId);
        Assert.Equal(end, interval.EndRawMessageId);
        await AssertCommitted(db, [start, end]);
    }

    [Fact]
    public async Task No_facts_commit_while_store_is_busy()
    {
        var id = await Text("empty", "Дякуємо за увагу.", At);
        await using var held = await Factory.CreateDbContextAsync();
        await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
        Assert.Equal(0, await Processor(new FixedParser()).ProcessAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        await using var db = await Factory.CreateDbContextAsync();
        await AssertCommitted(db, [id], 0);
    }

    [Fact]
    public async Task Watchdog_waits_for_store_and_respects_inprogress_history()
    {
        var id = await Text("watch-initial", "Шахеди на Сумщині курсом на Полтавщину.", At);
        Assert.Equal(1, await Processor().ProcessAsync(id, CancellationToken.None));
        var pending = await Text("watch-next", "Шахеди на Сумщині курсом на Полтавщину.", At.AddMinutes(1));
        var claims = Services.GetRequiredService<RawMessageClaims>();
        Assert.True(await claims.ClaimAsync(pending, "slow", CancellationToken.None));
        var watchdog = ActivatorUtilities.CreateInstance<TrackWatchdog>(Services);
        Task sweep;
        await using (var held = await Factory.CreateDbContextAsync())
        await using (var tx = await held.Database.BeginTransactionAsync())
        {
            await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
            sweep = watchdog.SweepAsync(CancellationToken.None);
            await WaitForStoreWaiters(1);
            await tx.CommitAsync();
        }
        await sweep.WaitAsync(TimeSpan.FromSeconds(20));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(TrackStatus.Active, (await db.TargetTracks.SingleAsync()).Status);
        Assert.Equal(1, await db.TargetTrackRevisions.CountAsync());
        var slow = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("slow"));
        Assert.Equal(1, await slow.ProcessAsync(pending, CancellationToken.None));
        await Task.WhenAll(watchdog.SweepAsync(CancellationToken.None), watchdog.SweepAsync(CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(20));
        db.ChangeTracker.Clear();
        Assert.Equal(TrackStatus.Closed, (await db.TargetTracks.SingleAsync()).Status);
        Assert.Equal(3, await db.TargetTrackRevisions.CountAsync()); // two evidence revisions + one expiry
    }

    [Fact]
    public async Task Reset_waits_for_inflight_pending_processor_then_requeues_its_committed_result()
    {
        var id = await Text("reset", "Шахеди на Сумщині курсом на Полтавщину.", At);
        var parser = new PausedParser(Services.GetRequiredService<RuleParser>());
        var process = Processor(parser).ProcessAsync(id, CancellationToken.None);
        await parser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var reset = Services.GetRequiredService<ReprocessService>().ResetAsync(CancellationToken.None);
        Task? sweep = null;
        try
        {
            await WaitForLock("AccessExclusiveLock");
            sweep = ActivatorUtilities.CreateInstance<TrackWatchdog>(Services).SweepAsync(CancellationToken.None);
            await WaitForLock("AccessShareLock");
        }
        finally { parser.Release.TrySetResult(); }
        Assert.Equal(1, await process.WaitAsync(TimeSpan.FromSeconds(30)));
        await reset.WaitAsync(TimeSpan.FromSeconds(30));
        await sweep!.WaitAsync(TimeSpan.FromSeconds(30));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(ProcessingStatus.Pending, (await db.RawMessages.SingleAsync()).ProcessingStatus);
        Assert.Empty(await db.Targets.ToListAsync());
        Assert.Empty(await db.TargetTracks.ToListAsync());
        Assert.Equal(1, await Processor().ProcessAsync(id, CancellationToken.None));
        await AssertCommitted(db, [id]);
    }

    [Theory]
    [InlineData("puluj", "1")]
    [InlineData("puluj_test", null)]
    public async Task Fixture_rejects_unsafe_external_reset_before_connecting(string database, string? optIn)
    {
        var previousConnection = Environment.GetEnvironmentVariable("PULUJ_TEST_CONNECTION");
        var previousOptIn = Environment.GetEnvironmentVariable("PULUJ_TEST_ALLOW_RESET");
        try
        {
            Environment.SetEnvironmentVariable("PULUJ_TEST_CONNECTION", $"Host=127.0.0.1;Database={database}");
            Environment.SetEnvironmentVariable("PULUJ_TEST_ALLOW_RESET", optIn);
            var guarded = new PipelineFixture();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.InitializeAsync());
            Assert.StartsWith("External integration DB", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PULUJ_TEST_CONNECTION", previousConnection);
            Environment.SetEnvironmentVariable("PULUJ_TEST_ALLOW_RESET", previousOptIn);
        }
    }

    private RawMessageProcessor Processor(IParser? parser = null) => parser is null
        ? ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("p00"))
        : ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("p00"), parser);

    [Theory]
    [InlineData("reprocess.sql")]
    [InlineData("reprocess-alerts.sql")]
    public async Task Maintenance_reset_fences_inflight_structured_writer(string script)
    {
        var id = await Structured("script-start", "script-alert", false);
        await using var maintenance = await Factory.CreateDbContextAsync();
        Task<int> process;
        Task<int> reset;
        await using (var held = await Factory.CreateDbContextAsync())
        await using (var tx = await held.Database.BeginTransactionAsync())
        {
            await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
            process = Processor().ProcessAsync(id, CancellationToken.None);
            await WaitForStoreWaiters(1);
            reset = maintenance.Database.ExecuteSqlRawAsync(await ReadScript(script));
            await WaitForLock("AccessExclusiveLock");
            await tx.CommitAsync();
        }
        Assert.Equal(1, await process.WaitAsync(TimeSpan.FromSeconds(30)));
        await reset.WaitAsync(TimeSpan.FromSeconds(30));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(ProcessingStatus.Pending, (await db.RawMessages.SingleAsync()).ProcessingStatus);
        Assert.Equal(0, await db.Targets.CountAsync());
        Assert.Equal(0, await db.AirAlerts.CountAsync());
    }

    [Fact]
    public async Task Maintenance_alert_repair_waits_for_store()
    {
        var id = await Structured("repair-start", "repair-alert", false);
        Assert.Equal(1, await Processor().ProcessAsync(id, CancellationToken.None));
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE air_alerts SET source_alert_id = 'text:repair', ended_at = started_at - interval '1 minute'");
        await using var maintenance = await Factory.CreateDbContextAsync();
        Task<int> repair;
        await using (var held = await Factory.CreateDbContextAsync())
        await using (var tx = await held.Database.BeginTransactionAsync())
        {
            await held.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
            repair = maintenance.Database.ExecuteSqlRawAsync(await ReadScript("fix-text-alert-ends.sql"));
            await WaitForStoreWaiters(1);
            await tx.CommitAsync();
        }
        await repair.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null((await db.AirAlerts.SingleAsync()).EndedAt);
    }

    private static Task<string> ReadScript(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Puluj.sln"))) root = root.Parent;
        return File.ReadAllTextAsync(Path.Combine(root!.FullName, "scripts", name));
    }

    private async Task<long> Text(string key, string text, DateTimeOffset at, string source = "tg_kpszsu")
    {
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Where(s => s.Code == source).Select(s => s.SourceId).SingleAsync();
        var result = await Services.GetRequiredService<RawMessageIngestor>().IngestAsync(new()
        {
            SourceId = sourceId, SourceMessageId = key, RawText = text,
            RawPayload = JsonSerializer.SerializeToDocument(new { test = key }), PublishedAt = at
        }, source, CancellationToken.None, announceProcessor: false);
        Assert.True(result.IsNew);
        return result.RawMessageId!.Value;
    }

    private async Task<long> Structured(string key, string alertId, bool end)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Where(s => s.Code == "alerts_in_ua").Select(s => s.SourceId).SingleAsync();
        var result = await Services.GetRequiredService<RawMessageIngestor>().IngestAsync(new()
        {
            SourceId = sourceId, SourceMessageId = key, PublishedAt = end ? At.AddMinutes(10) : At,
            RawPayload = JsonSerializer.SerializeToDocument(new
            {
                kind = end ? "alert.finished" : "alert.started", at = end ? At.AddMinutes(10) : At, test = key,
                alert = new { id = alertId, location_title = "Сумська область", location_oblast = "Сумська область", location_type = "oblast", alert_type = "air_raid", started_at = At }
            })
        }, "alerts_in_ua", CancellationToken.None, announceProcessor: false);
        Assert.True(result.IsNew);
        return result.RawMessageId!.Value;
    }

    private static async Task AssertCommitted(PulujDbContext db, List<long> ids, int targetsPerRoot = 1)
    {
        var rows = await db.RawMessages.AsNoTracking().Where(r => ids.Contains(r.RawMessageId)).ToListAsync();
        Assert.Equal(ids.Count, rows.Count);
        Assert.All(rows, r => { Assert.Equal(ProcessingStatus.Processed, r.ProcessingStatus); Assert.Equal(1, r.Attempts); });
        Assert.Equal(ids.Count * targetsPerRoot, await db.Targets.CountAsync(t => ids.Contains(t.RawMessageId)));
        Assert.Equal(0, await db.ProcessingErrors.CountAsync(e => ids.Contains(e.RawMessageId!.Value)));
    }

    private async Task WaitForStoreWaiters(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db = await Factory.CreateDbContextAsync();
        while (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())").SingleAsync(timeout.Token) < count)
            await Task.Delay(20, timeout.Token);
    }

    private async Task WaitForLock(string mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db = await Factory.CreateDbContextAsync();
        while (!await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation = 'raw_messages'::regclass AND mode = {mode} AND NOT granted) AS \"Value\"").SingleAsync(timeout.Token))
            await Task.Delay(20, timeout.Token);
    }

    private sealed class FixedParser(params ParsedFact[] facts) : IParser
    {
        public Task<IReadOnlyList<ParsedFact>> ParseAsync(NormalizedMessage message, ParseContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<ParsedFact>>(facts);
    }

    private sealed class PausedParser(IParser inner) : IParser
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ParsedFact>> ParseAsync(NormalizedMessage message, ParseContext context, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return await inner.ParseAsync(message, context, ct);
        }
    }
}
