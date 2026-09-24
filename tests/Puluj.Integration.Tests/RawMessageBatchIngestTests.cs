using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>Page-sized store used by the Telegram history load: one round trip, one transaction with the checkpoint.</summary>
[Collection(PipelineCollection.Name)]
public sealed class RawMessageBatchIngestTests(PipelineFixture fixture) : IAsyncLifetime
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private RawMessageIngestor Ingestor => Services.GetRequiredService<RawMessageIngestor>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDataAsync();
    public Task DisposeAsync() => InitializeAsync();

    [Fact]
    public async Task Batch_stores_in_input_order_skips_known_identities_and_commits_checkpoint_together()
    {
        var sourceId = await SourceIdAsync();
        var first = await Ingestor.IngestAsync(Message(sourceId, "10", "already there"), "tg_kpszsu", CancellationToken.None, announceProcessor: false);

        var page = new[]
        {
            Message(sourceId, "10", "already there"),
            Message(sourceId, "11", "eleven"),
            Message(sourceId, "12", "twelve", edit: 1700000000),
            Message(sourceId, "12", "twelve", edit: 1700000000),
            Message(sourceId, "13", "thirteen"),
        };
        var cursor = JsonSerializer.SerializeToDocument(new { history = new { lastId = 13 } });
        var results = await Ingestor.IngestBatchAsync(page, "tg_kpszsu", CancellationToken.None, announceProcessor: false,
            inSameTransaction: (db, ct) => CheckpointAsync(db, sourceId, "13", cursor, ct));

        Assert.Equal(page.Length, results.Count);
        Assert.False(results[0].IsNew);
        Assert.True(results[1].IsNew);
        Assert.True(results[2].IsNew);
        Assert.False(results[3].IsNew); // repeated identity within the page counts once
        Assert.True(results[4].IsNew);
        Assert.True(results[1].RawMessageId < results[2].RawMessageId && results[2].RawMessageId < results[4].RawMessageId);

        await using var check = await Factory.CreateDbContextAsync();
        var rows = await check.RawMessages.Where(m => m.SourceId == sourceId).OrderBy(m => m.RawMessageId)
            .Select(m => new { m.RawMessageId, m.SourceMessageId, m.SourceMessageKey, m.SourceRevision, m.RawText }).ToListAsync();
        Assert.Equal(["10", "11", "12:e1700000000", "13"], rows.Select(r => r.SourceMessageId));
        Assert.Equal(first.RawMessageId, rows[0].RawMessageId);
        Assert.Equal(("12", "e1700000000", "twelve"), (rows[2].SourceMessageKey, rows[2].SourceRevision, rows[2].RawText));
        var state = await check.CollectorStates.SingleAsync(s => s.SourceId == sourceId);
        Assert.Equal("13", state.LastSourceMessageId);
        Assert.Equal(13, state.Cursor!.RootElement.GetProperty("history").GetProperty("lastId").GetInt32());
    }

    [Fact]
    public async Task Edit_with_unchanged_text_is_not_stored_but_a_real_edit_is()
    {
        var sourceId = await SourceIdAsync();
        var original = await Ingestor.IngestAsync(Message(sourceId, "30", "КАБи на Дніпропетровщину"), "tg_kpszsu", CancellationToken.None, announceProcessor: false);
        var noOp = await Ingestor.IngestAsync(Message(sourceId, "30", "КАБи на Дніпропетровщину", edit: 1790208504), "tg_kpszsu", CancellationToken.None, announceProcessor: false);
        var real = await Ingestor.IngestAsync(Message(sourceId, "30", "КАБи на Дніпропетровщину та Запоріжжя", edit: 1790208600), "tg_kpszsu", CancellationToken.None, announceProcessor: false);
        var back = await Ingestor.IngestAsync(Message(sourceId, "30", "КАБи на Дніпропетровщину", edit: 1790208700), "tg_kpszsu", CancellationToken.None, announceProcessor: false);

        Assert.True(original.IsNew);
        Assert.Equal(new IngestResult(null, false), noOp);
        Assert.True(real.IsNew);
        Assert.True(back.IsNew); // compared with the latest revision, not with any earlier one

        await using var check = await Factory.CreateDbContextAsync();
        var revisions = await check.RawMessages.Where(m => m.SourceId == sourceId && m.SourceMessageKey == "30")
            .OrderBy(m => m.RawMessageId).Select(m => m.SourceRevision).ToListAsync();
        Assert.Equal(["0", "e1790208600", "e1790208700"], revisions);
    }

    [Fact]
    public async Task Batch_skips_edits_with_unchanged_text_against_the_database_and_the_page()
    {
        var sourceId = await SourceIdAsync();
        await Ingestor.IngestAsync(Message(sourceId, "40", "forty"), "tg_kpszsu", CancellationToken.None, announceProcessor: false);

        var results = await Ingestor.IngestBatchAsync(
        [
            Message(sourceId, "40", "forty", edit: 1700000100),
            Message(sourceId, "41", "forty-one"),
            Message(sourceId, "41", "forty-one", edit: 1700000200),
            Message(sourceId, "42", null, edit: 1700000300),
        ], "tg_kpszsu", CancellationToken.None, announceProcessor: false);

        Assert.Equal([false, true, false, true], results.Select(r => r.IsNew));
        await using var check = await Factory.CreateDbContextAsync();
        var rows = await check.RawMessages.Where(m => m.SourceId == sourceId).OrderBy(m => m.RawMessageId).Select(m => m.SourceMessageId).ToListAsync();
        Assert.Equal(["40", "41", "42:e1700000300"], rows);
    }

    [Fact]
    public async Task Batch_rolls_back_messages_when_the_checkpoint_fails()
    {
        var sourceId = await SourceIdAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ingestor.IngestBatchAsync([Message(sourceId, "20", "lost")], "tg_kpszsu", CancellationToken.None,
            announceProcessor: false, inSameTransaction: (_, _) => throw new InvalidOperationException("checkpoint failed")));

        await using var check = await Factory.CreateDbContextAsync();
        Assert.False(await check.RawMessages.AnyAsync(m => m.SourceId == sourceId && m.SourceMessageKey == "20"));
    }

    [Fact]
    public async Task Empty_batch_only_runs_the_checkpoint()
    {
        var sourceId = await SourceIdAsync();
        var results = await Ingestor.IngestBatchAsync([], "tg_kpszsu", CancellationToken.None, announceProcessor: false,
            inSameTransaction: (db, ct) => CheckpointAsync(db, sourceId, "0", null, ct));

        Assert.Empty(results);
        await using var check = await Factory.CreateDbContextAsync();
        Assert.Equal("0", (await check.CollectorStates.SingleAsync(s => s.SourceId == sourceId)).LastSourceMessageId);
    }

    /// <summary>What CollectorStateStore.MarkSuccessAsync(db, …) does; collector_states survives the fixture reset, so upsert.</summary>
    private static async Task CheckpointAsync(PulujDbContext db, int sourceId, string lastId, JsonDocument? cursor, CancellationToken ct)
    {
        var state = await db.CollectorStates.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct);
        if (state is null)
        {
            state = new CollectorState { SourceId = sourceId };
            db.CollectorStates.Add(state);
        }
        state.LastSourceMessageId = lastId;
        state.Cursor = cursor;
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> SourceIdAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
    }

    private static IncomingMessage Message(int sourceId, string id, string? text, long? edit = null) => new()
    {
        SourceId = sourceId,
        SourceMessageId = edit is null ? id : $"{id}:e{edit}",
        SourceMessageKey = id,
        SourceRevision = edit is null ? "0" : $"e{edit}",
        PublishedAt = At,
        RawText = text,
        RawPayload = JsonSerializer.SerializeToDocument(new { test = id }),
        Url = $"https://t.me/kpszsu/{id}",
    };
}
