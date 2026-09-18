using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing;
using Puluj.Processing.Pipeline;

namespace Puluj.Integration.Tests;

/// <summary>
/// Several processor instances over one database: claims hand every raw message to exactly one of them, expired
/// claims come back, and a row taken by one instance is not processed by another.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class ParallelProcessingTests(PipelineFixture fixture)
{
    private ServiceProvider? Services => fixture.Services;

    [Fact]
    public async Task Concurrent_claimers_process_each_message_exactly_once()
    {
        if (Services is null)
        {
            return;
        }
        var claims = Services.GetRequiredService<RawMessageClaims>();
        var ids = await IngestAsync("par1", 60);
        var processors = new Dictionary<string, RawMessageProcessor>
        {
            ["A"] = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("A")),
            ["B"] = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("B")),
        };
        // Two instances with two workers each, all draining the same backlog.
        var workers = new[] { "A", "A", "B", "B" }.Select(async name =>
        {
            var done = 0;
            while (await claims.ClaimOldestAsync(name, CancellationToken.None) is { } id)
            {
                await processors[name].ProcessAsync(id, CancellationToken.None);
                done++;
            }
            return done;
        }).ToArray();
        var counts = await Task.WhenAll(workers);
        Assert.True(counts.Sum() >= ids.Count, $"claimed {counts.Sum()} < {ids.Count}");

        await using var db = await Factory.CreateDbContextAsync();
        var rows = await db.RawMessages.AsNoTracking().Where(r => ids.Contains(r.RawMessageId)).ToListAsync();
        Assert.Equal(ids.Count, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(ProcessingStatus.Processed, r.ProcessingStatus);
            Assert.Contains(r.ClaimedBy, new[] { "A", "B" });
            Assert.Equal(1, r.Attempts);
        });
        var perMessage = await db.Targets.AsNoTracking().Where(t => ids.Contains(t.RawMessageId)).GroupBy(t => t.RawMessageId).Select(g => g.Count()).ToListAsync();
        Assert.Equal(ids.Count, perMessage.Count);
        Assert.All(perMessage, n => Assert.Equal(1, n)); // one sighting per message, never two
        Assert.Equal(0, await db.ProcessingErrors.CountAsync(e => ids.Contains(e.RawMessageId!.Value)));
    }

    [Fact]
    public async Task Store_serializes_derived_transactions()
    {
        if (Services is null)
        {
            return;
        }
        await using var first = await Factory.CreateDbContextAsync();
        await using var firstTx = await first.Database.BeginTransactionAsync();
        await first.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));

        var contended = TakeStoreAsync();
        Assert.NotSame(contended, await Task.WhenAny(contended, Task.Delay(200)));

        await firstTx.CommitAsync();
        await contended;
    }

    [Fact]
    public async Task Expired_claims_return_to_pending_and_count_the_attempt()
    {
        if (Services is null)
        {
            return;
        }
        var claims = Services.GetRequiredService<RawMessageClaims>();
        var max = Services.GetRequiredService<IOptions<ProcessingOptions>>().Value.MaxAttempts;
        var ids = await IngestAsync("lease", 3);
        var (expired, lastChance, alive) = (ids[0], ids[1], ids[2]);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE raw_messages SET processing_status = 4, claimed_by = 'dead', claimed_at = now() - interval '10 minutes', attempts = 0 WHERE raw_message_id = {expired}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE raw_messages SET processing_status = 4, claimed_by = 'dead', claimed_at = now() - interval '10 minutes', attempts = {max - 1} WHERE raw_message_id = {lastChance}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE raw_messages SET processing_status = 4, claimed_by = 'busy', claimed_at = now(), attempts = 0 WHERE raw_message_id = {alive}");
        }

        Assert.Equal(2, await claims.ReclaimExpiredAsync(CancellationToken.None));

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var a = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == expired);
            Assert.Equal(ProcessingStatus.Pending, a.ProcessingStatus);
            Assert.Equal(1, a.Attempts);
            Assert.Null(a.ClaimedBy);
            Assert.Null(a.ClaimedAt);

            var b = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == lastChance);
            Assert.Equal(ProcessingStatus.Failed, b.ProcessingStatus);
            Assert.Equal(max, b.Attempts);
            Assert.NotNull(b.ProcessedAt);
            Assert.Equal(1, await db.ProcessingErrors.CountAsync(e => e.RawMessageId == lastChance && e.Stage == "lease"));

            var c = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == alive);
            Assert.Equal(ProcessingStatus.InProgress, c.ProcessingStatus);
            Assert.Equal("busy", c.ClaimedBy);
        }
        // The returned one is claimable again and processes normally.
        Assert.True(await claims.ClaimAsync(expired, "A", CancellationToken.None));
        var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("A"));
        Assert.Equal(1, await processor.ProcessAsync(expired, CancellationToken.None));
    }

    [Fact]
    public async Task Processor_skips_rows_claimed_by_another_instance()
    {
        if (Services is null)
        {
            return;
        }
        var claims = Services.GetRequiredService<RawMessageClaims>();
        var id = (await IngestAsync("own", 1))[0];
        var a = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("A"));
        var b = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("B"));

        Assert.True(await claims.ClaimAsync(id, "A", CancellationToken.None));
        Assert.False(await claims.ClaimAsync(id, "B", CancellationToken.None)); // already taken
        Assert.Equal(0, await b.ProcessAsync(id, CancellationToken.None)); // not mine
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
            Assert.Equal(ProcessingStatus.InProgress, row.ProcessingStatus);
            Assert.Equal("A", row.ClaimedBy);
        }
        Assert.Equal(1, await a.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(0, await a.ProcessAsync(id, CancellationToken.None)); // done
        await using (var db2 = await Factory.CreateDbContextAsync())
        {
            var row = await db2.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
            Assert.Equal(ProcessingStatus.Processed, row.ProcessingStatus);
            Assert.Equal("A", row.ClaimedBy);
        }
    }

    [Fact]
    public async Task Two_processing_loops_share_the_backlog()
    {
        if (Services is null)
        {
            return;
        }
        var ids = await IngestAsync("loops", 40);
        var loops = new[] { "loop-A", "loop-B" }.Select(name =>
        {
            var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity(name));
            return ActivatorUtilities.CreateInstance<ProcessingLoop>(Services, new ProcessorIdentity(name), processor);
        }).ToList();
        foreach (var loop in loops)
        {
            await loop.StartAsync(CancellationToken.None);
        }
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                await using var db = await Factory.CreateDbContextAsync();
                if (await db.RawMessages.CountAsync(r => ids.Contains(r.RawMessageId) && r.ProcessingStatus == ProcessingStatus.Processed) == ids.Count)
                {
                    break;
                }
                await Task.Delay(250);
            }
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var loop in loops)
            {
                await loop.StopAsync(cts.Token);
            }
        }

        await using var check = await Factory.CreateDbContextAsync();
        var rows = await check.RawMessages.AsNoTracking().Where(r => ids.Contains(r.RawMessageId)).ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.Equal(ProcessingStatus.Processed, r.ProcessingStatus);
            Assert.Contains(r.ClaimedBy, new[] { "loop-A", "loop-B" });
        });
        // Both instances took part: with 40 messages and two workers each, one instance never gets them all.
        Assert.Equal(2, rows.Select(r => r.ClaimedBy).Distinct().Count());
        var perMessage = await check.Targets.AsNoTracking().Where(t => ids.Contains(t.RawMessageId)).GroupBy(t => t.RawMessageId).Select(g => g.Count()).ToListAsync();
        Assert.Equal(ids.Count, perMessage.Count);
        Assert.All(perMessage, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task Failed_message_counts_the_attempt_under_the_row_lock_and_ends_failed()
    {
        if (Services is null)
        {
            return;
        }
        var max = Services.GetRequiredService<IOptions<ProcessingOptions>>().Value.MaxAttempts;
        var ingestor = Services.GetRequiredService<RawMessageIngestor>();
        int sourceId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "alerts_in_ua").Select(s => s.SourceId).SingleAsync();
        }
        // An alerts.in.ua payload without the alert object: the handler throws inside the transaction.
        var broken = await ingestor.IngestAsync(new IncomingMessage
        {
            SourceId = sourceId,
            SourceMessageId = "broken:start",
            PublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
            RawPayload = JsonDocument.Parse("{\"kind\":\"alert.started\"}"),
        }, "alerts_in_ua", CancellationToken.None, announceProcessor: false);
        var id = broken.RawMessageId!.Value;
        var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("A"));

        for (var attempt = 1; attempt <= max; attempt++)
        {
            Assert.Equal(0, await processor.ProcessAsync(id, CancellationToken.None));
            await using var db = await Factory.CreateDbContextAsync();
            var row = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt, await db.ProcessingErrors.CountAsync(e => e.RawMessageId == id && e.Stage == "process"));
            if (attempt < max)
            {
                Assert.Equal(ProcessingStatus.Pending, row.ProcessingStatus); // free for any instance to retry
                Assert.Null(row.ClaimedBy);
            }
            else
            {
                Assert.Equal(ProcessingStatus.Failed, row.ProcessingStatus);
                Assert.Equal("A", row.ClaimedBy);
                Assert.NotNull(row.ProcessedAt);
            }
        }
        Assert.Equal(0, await processor.ProcessAsync(id, CancellationToken.None)); // Failed rows are left alone
    }

    [Fact]
    public async Task Transient_database_failure_returns_the_message_to_pending_without_counting_the_attempt()
    {
        if (Services is null)
        {
            return;
        }
        var id = (await IngestAsync("transient", 1)).Single();
        // A deadlock raised from inside the store stage (as by a writer that does not hold the store lock), once.
        var sink = new DeadlockOnceSink();
        var sinks = new List<ITargetSink> { sink };
        var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("T"), sinks);

        Assert.Equal(0, await processor.ProcessAsync(id, CancellationToken.None));
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
            Assert.Equal(ProcessingStatus.Pending, row.ProcessingStatus);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.ClaimedBy);
            var error = await db.ProcessingErrors.SingleAsync(e => e.RawMessageId == id);
            Assert.Equal("transient", error.Stage);
            Assert.StartsWith("40P01", error.Message);
            Assert.Null(error.Exception);
            Assert.Equal(0, await db.Targets.CountAsync(t => t.RawMessageId == id)); // rolled back with the savepoint
        }

        Assert.Equal(1, await processor.ProcessAsync(id, CancellationToken.None)); // the retry goes through
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
            Assert.Equal(ProcessingStatus.Processed, row.ProcessingStatus);
            Assert.Equal(1, row.Attempts);
            Assert.Equal("T", row.ClaimedBy);
        }
        Assert.Equal(2, sink.Calls);
    }

    private sealed class DeadlockOnceSink : ITargetSink
    {
        public int Calls;

        public Task OnTargetsAsync(PulujDbContext db, IReadOnlyList<Domain.Entities.Target> targets, Domain.Entities.Source source, ICollection<Infrastructure.Notifications.PulujEvent> events, CancellationToken ct)
        {
            if (++Calls == 1)
            {
                throw new DbUpdateException("save failed", new Npgsql.PostgresException("deadlock detected", "ERROR", "ERROR", "40P01"));
            }
            return Task.CompletedTask;
        }
    }

    private IDbContextFactory<PulujDbContext> Factory => Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();

    private async Task TakeStoreAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store));
        await tx.CommitAsync();
    }

    /// <summary>Stores <paramref name="count"/> Pending sightings, ten minutes apart, without a NOTIFY.</summary>
    private async Task<List<long>> IngestAsync(string prefix, int count)
    {
        var ingestor = Services!.GetRequiredService<RawMessageIngestor>();
        int sourceId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
        }
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var result = await ingestor.IngestAsync(new IncomingMessage
            {
                SourceId = sourceId,
                SourceMessageId = $"{prefix}-{i}",
                PublishedAt = t0.AddMinutes(10 * i),
                RawText = $"Шахеди на Сумщині курсом на Полтавщину. #{prefix}{i}",
                RawPayload = JsonDocument.Parse("{\"kind\":\"test\"}"),
            }, "tg_kpszsu", CancellationToken.None, announceProcessor: false);
            Assert.True(result.IsNew);
            ids.Add(result.RawMessageId!.Value);
        }
        return ids;
    }
}
