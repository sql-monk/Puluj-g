using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Collectors;

/// <summary>Small helper around CollectorState rows (cursor + health, spec §29 watchdog).</summary>
public sealed class CollectorStateStore(IDbContextFactory<PulujDbContext> factory, TimeProvider clock)
{
    public async Task<CollectorState> GetAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.CollectorStates.AsNoTracking().FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
               ?? new CollectorState { SourceId = sourceId };
    }

    public Task MarkPolledAsync(int sourceId, CancellationToken ct) =>
        UpdateAsync(sourceId, s => s.LastPolledAt = clock.GetUtcNow(), ct);

    public Task MarkSuccessAsync(int sourceId, string? lastSourceMessageId, DateTimeOffset? lastMessageAt, JsonDocument? cursor, CancellationToken ct) =>
        UpdateAsync(sourceId, MarkSuccess(lastSourceMessageId, lastMessageAt, cursor), ct);

    /// <summary>Same as <see cref="MarkSuccessAsync(int, string?, DateTimeOffset?, JsonDocument?, CancellationToken)"/> on a caller's context, so the checkpoint commits in the caller's transaction.</summary>
    public Task MarkSuccessAsync(PulujDbContext db, int sourceId, string? lastSourceMessageId, DateTimeOffset? lastMessageAt, JsonDocument? cursor, CancellationToken ct) =>
        UpdateAsync(db, sourceId, MarkSuccess(lastSourceMessageId, lastMessageAt, cursor), ct);

    private Action<CollectorState> MarkSuccess(string? lastSourceMessageId, DateTimeOffset? lastMessageAt, JsonDocument? cursor) =>
        s =>
        {
            var now = clock.GetUtcNow();
            s.LastPolledAt = now;
            s.LastSuccessAt = now;
            s.LastError = null;
            s.ConsecutiveFailures = 0;
            if (lastSourceMessageId is not null)
            {
                s.LastSourceMessageId = lastSourceMessageId;
            }
            if (lastMessageAt is not null && (s.LastMessageAt is null || lastMessageAt > s.LastMessageAt))
            {
                s.LastMessageAt = lastMessageAt;
            }
            if (cursor is not null)
            {
                s.Cursor = cursor;
            }
        };

    public Task MarkFailureAsync(int sourceId, string error, CancellationToken ct) =>
        UpdateAsync(sourceId, s =>
        {
            s.LastPolledAt = clock.GetUtcNow();
            s.LastError = error.Length > 2000 ? error[..2000] : error;
            s.ConsecutiveFailures++;
        }, ct);

    /// <summary>Persists scheduler metadata without clearing a failure that was recorded immediately before it.</summary>
    public Task MarkCursorAsync(int sourceId, JsonDocument cursor, CancellationToken ct) =>
        UpdateAsync(sourceId, s =>
        {
            s.LastPolledAt = clock.GetUtcNow();
            s.Cursor = cursor;
        }, ct);

    private async Task UpdateAsync(int sourceId, Action<CollectorState> mutate, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await UpdateAsync(db, sourceId, mutate, ct);
    }

    private static async Task UpdateAsync(PulujDbContext db, int sourceId, Action<CollectorState> mutate, CancellationToken ct)
    {
        var state = await db.CollectorStates.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct);
        if (state is null)
        {
            state = new CollectorState { SourceId = sourceId };
            db.CollectorStates.Add(state);
        }
        mutate(state);
        await db.SaveChangesAsync(ct);
    }
}
