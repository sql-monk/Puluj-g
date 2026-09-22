using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Notifications;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;

namespace Puluj.Processing.Correlation;

/// <summary>
/// Closes tracks that have not been updated for 2x their class correlation window (spec §12 fading ends in closure).
/// The closure is stamped at event time (last seen + the timeout), never at wall-clock time, so a replay sees the
/// track end when it faded. While the pipeline is behind (a rebuild or a history load: pending messages older than
/// half an hour), "now" is the oldest pending message's time, so tracks of 2022 are not closed under the feet of the
/// 2022 messages still to come. The sweep takes Store, so it never races a message being
/// stored; every processor instance runs one, the others simply find nothing to close.
/// </summary>
public sealed class TrackWatchdog(
    IDbContextFactory<PulujDbContext> factory,
    IndexProvider indexes,
    INotifyPublisher notifier,
    IOptionsMonitor<CorrelationOptions> options,
    TimeProvider clock,
    ILogger<TrackWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await indexes.Ready.WaitAsync(ct);
        using var timer = new PeriodicTimer(options.CurrentValue.WatchdogInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Track watchdog sweep failed");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var wall = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Reset fences the raw table before Store. Take the read lock in the same order, not inside Store.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE raw_messages IN ACCESS SHARE MODE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store), ct);
        // One min() per status, not "status IN (Pending, InProgress)": each single-status predicate matches a partial
        // index (ix_raw_messages_pending_published, ix_raw_messages_in_progress_claimed_at) and costs a few pages, while
        // the OR made every sweep a parallel seq scan of raw_messages under the Store lock.
        var oldestPending = await db.RawMessages.AsNoTracking()
            .Where(r => r.ProcessingStatus == ProcessingStatus.Pending)
            .MinAsync(r => (DateTimeOffset?)r.PublishedAt, ct);
        var oldestInProgress = await db.RawMessages.AsNoTracking()
            .Where(r => r.ProcessingStatus == ProcessingStatus.InProgress)
            .MinAsync(r => (DateTimeOffset?)r.PublishedAt, ct);
        if (oldestInProgress is { } ip && (oldestPending is null || ip < oldestPending))
        {
            oldestPending = ip;
        }
        var now = oldestPending is { } p && p < wall.AddMinutes(-30) ? p : wall;
        var active = await db.TargetTracks.Where(t => t.Status == TrackStatus.Active).ToListAsync(ct);
        var closed = new List<long>();
        foreach (var t in active)
        {
            var window = indexes.Taxonomy.ClassProfile(t.TargetClassId)?.CorrelationWindowMinutes ?? 30;
            var closeAt = t.LastSeenAt + TimeSpan.FromMinutes(window * options.CurrentValue.CloseAfterWindows);
            if (closeAt > now)
            {
                continue;
            }
            t.Status = TrackStatus.Closed;
            t.ClosedReason = "timeout";
            t.UpdatedAt = closeAt > t.UpdatedAt ? closeAt : t.UpdatedAt;
            db.TargetTrackRevisions.Add(TrackUpdater.Revision(t, null, t.UpdatedAt));
            closed.Add(t.TargetTrackId);
        }
        // Free-text alerts rarely get an explicit "відбій" for every raion: expire them after a few hours.
        var staleBefore = now - Structured.TextAlertSink.MaxAge;
        var expired = await db.AirAlerts
            .Where(a => a.EndedAt == null && a.SourceAlertId.StartsWith(Structured.TextAlertSink.KeyPrefix) && a.StartedAt < staleBefore)
            .ToListAsync(ct);
        foreach (var a in expired)
        {
            a.EndedAt = a.StartedAt + Structured.TextAlertSink.MaxAge;
        }
        if (closed.Count == 0 && expired.Count == 0)
        {
            return;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var id in closed)
        {
            await notifier.PublishAsync(new PulujEvent(PulujEventType.TrackClosed, id, now), ct);
        }
        foreach (var a in expired)
        {
            await notifier.PublishAsync(new PulujEvent(PulujEventType.AlertChanged, a.AirAlertId, now), ct);
        }
        logger.LogInformation("Watchdog closed {Count} stale track(s), expired {Alerts} text alert(s)", closed.Count, expired.Count);
    }
}
