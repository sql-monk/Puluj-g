using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Puluj.Api.Hubs;
using Puluj.Infrastructure.Notifications;

namespace Puluj.Api.Services;

/// <summary>
/// Turns Postgres NOTIFY events from the Worker into SignalR pushes. The database stays the source of truth: only ids
/// travel over NOTIFY. Only what the map can still show is pushed (MapOptions): a track last reported inside the
/// longest marker lifetime, a target inside the feed window, an alert that is open or ended recently. A history load
/// or a reprocess announces every old message the same way, and those would otherwise cost a few queries each and a
/// re-render on every client for nothing.
/// </summary>
public sealed class NotifyBridge(
    PgNotifyListener listener,
    IHubContext<MapHub, IMapClient> hub,
    SnapshotService snapshots,
    ReferenceCache refs,
    IOptions<MapOptions> options,
    TimeProvider clock,
    ILogger<NotifyBridge> logger) : BackgroundService
{
    private static readonly TimeSpan SkipReportInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await refs.Ready.WaitAsync(ct);
        var skipped = 0;
        var skippedSince = clock.GetUtcNow();
        var failed = 0;
        var failedSince = clock.GetUtcNow();
        await foreach (var evt in listener.ListenAsync(ct))
        {
            try
            {
                logger.LogDebug("Event {Type} {Id}", evt.Type, evt.Id);
                var map = options.Value;
                var now = clock.GetUtcNow();
                var pushed = evt.Type switch
                {
                    PulujEventType.TrackUpserted => await PushTrackAsync(evt.Id, now - map.MaxLifetime, closed: false, ct),
                    PulujEventType.TrackClosed => await PushTrackAsync(evt.Id, now - map.MaxLifetime, closed: true, ct),
                    PulujEventType.TargetCreated => await PushTargetAsync(evt.Id, now - map.FeedWindow, ct),
                    PulujEventType.AlertChanged => await PushAlertAsync(evt.Id, now - map.MaxLifetime, ct),
                    PulujEventType.ListenerReconnected => await ResyncAsync(now),
                    _ => true,
                };
                if (!pushed)
                {
                    skipped++;
                }
                // One line a minute while old events stream past (a reprocess), nothing while they do not.
                if (skipped > 0 && now - skippedSince >= SkipReportInterval)
                {
                    logger.LogInformation("Skipped {Count} stale push event(s) outside the map windows (tracks {Lifetime}, feed {Feed})", skipped, map.MaxLifetime, map.FeedWindow);
                    skipped = 0;
                    skippedSince = now;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The first failure of a minute carries the exception; the rest are counted (a schema mismatch fails
                // every event the same way, and a reprocess announces thousands a minute).
                var now = clock.GetUtcNow();
                if (failed == 0 || now - failedSince >= SkipReportInterval)
                {
                    if (failed > 1)
                    {
                        logger.LogWarning("Failed to push {Count} more event(s) since {Since:HH:mm:ss}", failed - 1, failedSince);
                    }
                    logger.LogWarning(ex, "Failed to push {Type} {Id}", evt.Type, evt.Id);
                    failed = 0;
                    failedSince = now;
                }
                failed++;
            }
        }
    }

    /// <summary>False when the row is missing or outside the window (nothing pushed).</summary>
    private async Task<bool> PushTrackAsync(long id, DateTimeOffset notBefore, bool closed, CancellationToken ct)
    {
        if (await snapshots.TrackAsync(id, ct, notBefore) is not { } track)
        {
            return false;
        }
        await (closed ? hub.Clients.All.TrackClosed(track) : hub.Clients.All.TrackUpserted(track));
        return true;
    }

    private async Task<bool> PushTargetAsync(long id, DateTimeOffset notBefore, CancellationToken ct)
    {
        if (await snapshots.TargetAsync(id, ct, notBefore) is not { } target)
        {
            return false;
        }
        await hub.Clients.All.TargetCreated(target);
        return true;
    }

    /// <summary>NOTIFY is at-most-once: after a LISTEN reconnect every client of this replica reloads its window (the snapshot/checkpoint path, never the missed packets).</summary>
    private async Task<bool> ResyncAsync(DateTimeOffset now)
    {
        logger.LogInformation("LISTEN connection re-established: asking the clients to resync");
        await hub.Clients.All.Resync(now);
        return true;
    }

    private async Task<bool> PushAlertAsync(long id, DateTimeOffset endedNotBefore, CancellationToken ct)
    {
        if (await snapshots.AlertAsync(id, ct, endedNotBefore) is not { } alert)
        {
            return false;
        }
        await hub.Clients.All.AlertChanged(alert);
        return true;
    }
}
