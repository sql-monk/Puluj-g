using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Admin;
using Puluj.Admin.Endpoints;
using Puluj.Domain.Entities;
using Puluj.EntityAdmin;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.Integration.Tests;

/// <summary>
/// Paged reads behind the admin message browser (audit A01) and the LLM history (A10) on real PostgreSQL: pages follow
/// each other without gaps or repeats, a source filter keeps other sources out, and the LLM filter counts all matches.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class AdminReadQueriesTests(PipelineFixture fixture) : IAsyncLifetime
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDataAsync();
    public Task DisposeAsync() => fixture.ResetDataAsync();

    [Fact]
    public async Task A04_latest_success_uses_completion_order_and_ignores_null_completions()
    {
        var store = new EntityAdminStore(Services.GetRequiredService<IConfiguration>(), new SettingsStore(Factory, TimeProvider.System));
        Assert.Null((await store.QueueSnapshotAsync(CancellationToken.None)).LastSuccessAt);
        var older = await Message("tg_kpszsu", "completion-older", "Older delivery completes last");
        var newer = await Message("tg_kpszsu", "completion-newer", "Newer delivery completes first");
        var missingCompletion = await Message("tg_kpszsu", "completion-null", "Incomplete historical row");
        var pending = await Message("tg_kpszsu", "completion-pending", "Old pending delivery");
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var deliveries = await db.EntityDeliveries.ToDictionaryAsync(d => d.RawMessageId);
            deliveries[older].EnqueuedAt = At;
            deliveries[older].Status = "succeeded";
            deliveries[older].CompletedAt = now.AddSeconds(-2);
            deliveries[newer].EnqueuedAt = At.AddMinutes(1);
            deliveries[newer].Status = "succeeded";
            deliveries[newer].CompletedAt = now.AddHours(-2);
            deliveries[missingCompletion].EnqueuedAt = At.AddMinutes(2);
            deliveries[missingCompletion].Status = "succeeded";
            deliveries[missingCompletion].CompletedAt = null;
            deliveries[pending].EnqueuedAt = At.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var snapshot = await store.QueueSnapshotAsync(CancellationToken.None);
        Assert.Equal(now.AddSeconds(-2), snapshot.LastSuccessAt);
        Assert.Equal(1, snapshot.Queued);
        // A recent recovered success means the old backlog alone must not report a stopped extractor.
        Assert.Equal("ok", EntityExtractorStatus.Describe(new EeProbe(true, 200, null), snapshot, false, now).Status);
    }

    [Fact]
    public async Task A01_messages_of_one_source_page_newest_first_without_gaps()
    {
        var ids = new List<long>();
        for (var i = 0; i < 5; i++) ids.Add(await Message("tg_kpszsu", $"page-{i}", $"Повідомлення {i}"));
        await Message("alerts_in_ua", "other", "Інше джерело");
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();

        var first = await AdminReadQueries.MessagesAsync(db, sourceId, null, null, 2, CancellationToken.None);
        var second = await AdminReadQueries.MessagesAsync(db, sourceId, null, first.NextCursor, 2, CancellationToken.None);
        var third = await AdminReadQueries.MessagesAsync(db, sourceId, null, second.NextCursor, 2, CancellationToken.None);

        var seen = first.Messages.Concat(second.Messages).Concat(third.Messages).Select(m => m.Id).ToList();
        Assert.Equal(ids.AsEnumerable().Reverse(), seen);
        Assert.Null(third.NextCursor);
        Assert.All(first.Messages, m => Assert.Equal("tg_kpszsu", m.SourceCode));
        Assert.Equal("Повідомлення 4", first.Messages[0].Text);
        Assert.Equal("Pending", first.Messages[0].ProcessingStatus);

        var single = await AdminReadQueries.MessagesAsync(db, null, ids[2], null, 50, CancellationToken.None);
        Assert.Equal(ids[2], Assert.Single(single.Messages).Id);
    }

    [Fact]
    public async Task A10_llm_history_pages_past_the_first_hundred_and_filters_failures()
    {
        var message = await Message("tg_kpszsu", "llm-page", "Текст");
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
            for (var i = 0; i < 150; i++)
            {
                db.LlmRequests.Add(new LlmRequest
                {
                    RawMessageId = message, SourceId = sourceId, OccurredAt = At.AddMinutes(i), Worker = "test", Model = "m", PromptVersion = "v",
                    Outcome = i % 10 == 0 ? "invalid_response" : i % 2 == 0 ? "success" : "empty",
                    DurationMs = 1, RequestText = "t", SystemPrompt = "s",
                });
            }
            await db.SaveChangesAsync();
        }

        await using var read = await Factory.CreateDbContextAsync();
        var from = At.AddDays(-1);
        var to = At.AddDays(1);
        var first = await AdminReadQueries.LlmRequestsAsync(read, from, to, null, null, null, 100, CancellationToken.None);
        var second = await AdminReadQueries.LlmRequestsAsync(read, from, to, null, null, first.NextBeforeId, 100, CancellationToken.None);
        Assert.Equal(150, first.Total);
        Assert.Equal(100, first.Requests.Count);
        Assert.Equal(50, second.Requests.Count);
        Assert.Null(second.NextBeforeId);
        Assert.Empty(first.Requests.Select(r => r.Id).Intersect(second.Requests.Select(r => r.Id)));

        var failures = await AdminReadQueries.LlmRequestsAsync(read, from, to, "failures", null, null, 100, CancellationToken.None);
        Assert.Equal(15, failures.Total);
        Assert.All(failures.Requests, r => Assert.Equal("invalid_response", r.Outcome));

        var byMessage = await AdminReadQueries.LlmRequestsAsync(read, from, to, "failures", message.ToString(), null, 100, CancellationToken.None);
        Assert.Equal(15, byMessage.Total);
        var bySource = await AdminReadQueries.LlmRequestsAsync(read, from, to, null, "kpszsu", null, 10, CancellationToken.None);
        Assert.Equal(150, bySource.Total);
    }

    private async Task<long> Message(string source, string key, string text)
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
}
