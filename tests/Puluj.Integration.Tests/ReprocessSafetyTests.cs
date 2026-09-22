using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Pipeline;

namespace Puluj.Integration.Tests;

/// <summary>
/// A reprocess must not stall the collectors: the raw table is fenced only for a moment, the derived tables are
/// cleared holding Store alone, and the pause it sets is its own to lift — a history load's pause is left in place.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class ReprocessSafetyTests(PipelineFixture fixture) : IAsyncLifetime
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private ReprocessService Reprocess => Services.GetRequiredService<ReprocessService>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        await Reprocess.ResumeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Reprocess.BeforeDerivedDeletes = null;
        await Reprocess.ResumeAsync(CancellationToken.None);
        await fixture.ResetDataAsync();
    }

    [Fact]
    public async Task Derived_state_is_deleted_under_store_alone_while_ingestion_keeps_flowing()
    {
        var processed = await Text("a", "Шахеди на Сумщині курсом на Полтавщину.");
        Assert.Equal(1, await Processor().ProcessAsync(processed, CancellationToken.None));
        long? ingestedDuringDeletes = null;
        bool storeFreeDuringDeletes = true;
        int targetsBeforeDeletes = -1;
        ProcessingStatus statusDuringDeletes = ProcessingStatus.InProgress;
        Reprocess.BeforeDerivedDeletes = async ct =>
        {
            await using var db = await Factory.CreateDbContextAsync(ct);
            // The fence has committed: raw rows are Pending, derived rows still there, the table itself is free …
            statusDuringDeletes = (await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == processed, ct)).ProcessingStatus;
            targetsBeforeDeletes = await db.Targets.CountAsync(ct);
            ingestedDuringDeletes = await Text("b", "Ракета на Київ.").WaitAsync(TimeSpan.FromSeconds(10));
            // … and a Store writer (processor, watchdog) would wait: the session-level lock is held.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            storeFreeDuringDeletes = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLocks.Store}) AS \"Value\"").SingleAsync(ct);
        };

        var pending = await Reprocess.ResetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ProcessingStatus.Pending, statusDuringDeletes);
        Assert.Equal(1, targetsBeforeDeletes);
        Assert.NotNull(ingestedDuringDeletes);
        Assert.False(storeFreeDuringDeletes);
        Assert.Equal(2, pending);
        await using var after = await Factory.CreateDbContextAsync();
        Assert.Equal(0, await after.Targets.CountAsync());
        Assert.All(await after.RawMessages.ToListAsync(), r => Assert.Equal(ProcessingStatus.Pending, r.ProcessingStatus));
        Assert.Null(await Reprocess.PausedAsync(CancellationToken.None));
        await using var tx = await after.Database.BeginTransactionAsync();
        Assert.True(await after.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLocks.Store}) AS \"Value\"").SingleAsync(), "Store must be released after the reset");
    }

    [Fact]
    public async Task Finished_rows_are_reset_in_batches_including_the_boundary_case()
    {
        var count = ReprocessService.BatchSize + 1;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
            var messages = Enumerable.Range(0, count).Select(i => new IncomingMessage
            {
                SourceId = sourceId, SourceMessageId = $"batch-{i}", RawText = $"text {i}", PublishedAt = At.AddSeconds(i),
                RawPayload = JsonSerializer.SerializeToDocument(new { i }),
            }).ToList();
            var results = await Services.GetRequiredService<RawMessageIngestor>().IngestBatchAsync(messages, "tg_kpszsu", CancellationToken.None, announceProcessor: false);
            Assert.Equal(count, results.Count(r => r.IsNew));
            // Processed, Skipped and Failed alike; a stale InProgress claim of a dead instance is left to the fence.
            await db.Database.ExecuteSqlRawAsync("UPDATE raw_messages SET processing_status = 1 + (raw_message_id % 3), processed_at = now(), attempts = 1");
            await db.Database.ExecuteSqlRawAsync("UPDATE raw_messages SET processing_status = 4, claimed_by = 'dead', claimed_at = now() - interval '1 hour' WHERE raw_message_id = (SELECT max(raw_message_id) FROM raw_messages)");
        }

        var pending = await Reprocess.ResetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(count, pending);
        await using var after = await Factory.CreateDbContextAsync();
        Assert.Equal(count, await after.RawMessages.CountAsync(r => r.ProcessingStatus == ProcessingStatus.Pending && r.Attempts == 0 && r.ClaimedBy == null && r.ProcessedAt == null));
    }

    [Fact]
    public async Task A_history_load_pause_is_kept_and_a_reprocess_pause_is_lifted()
    {
        await Text("h", "Шахеди на Сумщині.");
        await Reprocess.PauseAsync("history load: 3 channel(s) since 2022-02-24", CancellationToken.None);
        await Reprocess.ResetAsync(CancellationToken.None);
        Assert.Equal("history load: 3 channel(s) since 2022-02-24", await Reprocess.PausedAsync(CancellationToken.None));

        // A pause left by a reset that did not finish is this service's own: the next reset runs over it and lifts it.
        await Reprocess.PauseAsync($"{ReprocessService.ReprocessPausePrefix} failed (test); run the reprocess again", CancellationToken.None);
        Assert.True(ReprocessService.OwnsPause((await Reprocess.PausedAsync(CancellationToken.None))!));
        await Reprocess.ResetAsync(CancellationToken.None);
        Assert.Null(await Reprocess.PausedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Script_lifts_only_its_own_pause_and_releases_store()
    {
        var id = await Text("s", "Шахеди на Сумщині курсом на Полтавщину.");
        Assert.Equal(1, await Processor().ProcessAsync(id, CancellationToken.None));
        await Reprocess.PauseAsync("history load: 1 channel(s) since 2022-02-24", CancellationToken.None);
        await using var maintenance = await Factory.CreateDbContextAsync();
        await maintenance.Database.ExecuteSqlRawAsync(await ReadScript("reprocess.sql"));
        Assert.Equal("history load: 1 channel(s) since 2022-02-24", await Reprocess.PausedAsync(CancellationToken.None));
        await Reprocess.ResumeAsync(CancellationToken.None);

        await maintenance.Database.ExecuteSqlRawAsync(await ReadScript("reprocess.sql"));
        Assert.Null(await Reprocess.PausedAsync(CancellationToken.None));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(ProcessingStatus.Pending, (await db.RawMessages.SingleAsync()).ProcessingStatus);
        Assert.Equal(0, await db.Targets.CountAsync());
        await using var tx = await db.Database.BeginTransactionAsync();
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLocks.Store}) AS \"Value\"").SingleAsync());
    }

    private RawMessageProcessor Processor() => ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("safety"));

    private async Task<long> Text(string key, string text, string source = "tg_kpszsu")
    {
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Where(s => s.Code == source).Select(s => s.SourceId).SingleAsync();
        var result = await Services.GetRequiredService<RawMessageIngestor>().IngestAsync(new()
        {
            SourceId = sourceId, SourceMessageId = key, RawText = text,
            RawPayload = JsonSerializer.SerializeToDocument(new { test = key }), PublishedAt = At,
        }, source, CancellationToken.None, announceProcessor: false);
        Assert.True(result.IsNew);
        return result.RawMessageId!.Value;
    }

    private static Task<string> ReadScript(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Puluj.sln"))) root = root.Parent;
        return File.ReadAllTextAsync(Path.Combine(root!.FullName, "scripts", name));
    }
}
