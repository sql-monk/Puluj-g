using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Pipeline;

namespace Puluj.Processing.Structured;

/// <summary>
/// Turns AirRaidAlert / AlertCancelled targets parsed from text ("Фастівський район — повітряна тривога, жовтий рівень")
/// into AirAlert intervals, so levelled regional alerts show on the map next to the alerts.in.ua ones.
/// One open interval per (source, place); a repeated message only updates the level.
/// </summary>
public sealed class TextAlertSink(TimeProvider clock, ILogger<TextAlertSink> logger) : ITargetSink
{
    public const string KeyPrefix = "text:";

    /// <summary>Text alerts with no "відбій" are dropped after this long (see TrackWatchdog).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    public async Task OnTargetsAsync(PulujDbContext db, IReadOnlyList<Target> targets, Source source, ICollection<PulujEvent> events, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var o in targets.OrderBy(x => x.ObservedAt))
        {
            if (o.IdentificationMethod == IdentificationMethod.Structured || o.LocationPlaceId is not int placeId)
            {
                continue;
            }
            if (o.EventType == EventType.AirRaidAlert)
            {
                // One key per interval ("text:<place>:<start>"): the (source, key) pair is unique, and a place gets
                // alerted again after an "відбій".
                var prefix = KeyPrefix + placeId + ":";
                var key = prefix + o.ObservedAt.ToUnixTimeSeconds();
                // The interval active at the message's time (event time, not the clock: a rebuild of old messages must
                // see the same intervals as live processing did), so a repeat neither reopens an ended alert nor
                // duplicates one that a history load stored already closed.
                var open = await db.AirAlerts
                    .Where(a => a.SourceId == source.SourceId && a.SourceAlertId.StartsWith(prefix)
                        && a.StartedAt <= o.ObservedAt && (a.EndedAt == null || a.EndedAt > o.ObservedAt))
                    .OrderByDescending(a => a.StartedAt)
                    .FirstOrDefaultAsync(ct);
                if (open is not null && o.ObservedAt - open.StartedAt > MaxAge)
                {
                    open.EndedAt = o.ObservedAt;
                    open = null;
                }
                if (open is null)
                {
                    // A cancellation may have committed before this late start. Stored facts are the evidence;
                    // include every ancestor, since an oblast cancellation also covers a grandchild settlement.
                    var ancestors = await db.Database.SqlQuery<int>($"""
                        WITH RECURSIVE scope AS (
                            SELECT place_id, parent_id FROM places WHERE place_id = {placeId}
                            UNION
                            SELECT p.place_id, p.parent_id FROM places p JOIN scope s ON p.place_id = s.parent_id
                        ) SELECT place_id AS "Value" FROM scope
                        """).ToListAsync(ct);
                    var cancellation = await db.Targets.Where(t => t.EventType == EventType.AlertCancelled
                            && t.IdentificationMethod != IdentificationMethod.Structured
                            && t.LocationPlaceId != null && ancestors.Contains(t.LocationPlaceId.Value)
                            && t.ObservedAt >= o.ObservedAt)
                        .OrderBy(t => t.ObservedAt).ThenBy(t => t.TargetId)
                        .Select(t => new { t.ObservedAt, t.RawMessageId }).FirstOrDefaultAsync(ct);
                    var cancelledAt = cancellation?.ObservedAt;
                    // The same start can be reported again after the interval is already closed.
                    if (await db.AirAlerts.AnyAsync(a => a.SourceId == source.SourceId && a.SourceAlertId == key, ct))
                    {
                        continue;
                    }
                    // A message older than MaxAge is history (a rebuild, a backfill): the interval is stored already
                    // expired, so an alert of months ago never shows open on the live map until the watchdog's next pass.
                    // A later "відбій" inside it still shortens it.
                    var history = now - o.ObservedAt > MaxAge;
                    var alert = new AirAlert
                    {
                        SourceId = source.SourceId,
                        SourceAlertId = key,
                        PlaceId = placeId,
                        AlertType = AirAlertType.AirRaid,
                        Level = o.AlertLevel,
                        StartedAt = o.ObservedAt,
                        EndedAt = history && (cancelledAt is null || cancelledAt > o.ObservedAt + MaxAge)
                            ? o.ObservedAt + MaxAge : cancelledAt,
                        StartRawMessageId = o.RawMessageId,
                        EndRawMessageId = cancellation is not null && (!history || cancelledAt <= o.ObservedAt + MaxAge)
                            ? cancellation.RawMessageId : null,
                    };
                    db.AirAlerts.Add(alert);
                    await db.SaveChangesAsync(ct);
                    events.Add(new PulujEvent(PulujEventType.AlertChanged, alert.AirAlertId, now));
                }
                else if (o.AlertLevel != AirAlertLevel.Unknown && open.Level != o.AlertLevel)
                {
                    open.Level = o.AlertLevel;
                    events.Add(new PulujEvent(PulujEventType.AlertChanged, open.AirAlertId, now));
                }
            }
            else if (o.EventType == EventType.AlertCancelled)
            {
                // "Відбій" for a place also ends the text alerts of the places inside it (raion towns inside their oblast).
                // Only what was in force at that moment: a replayed old "відбій" must not close an alert that started
                // after it, and one that a history load stored already expired is shortened to the real end.
                var descendants = await db.Database.SqlQuery<int>($"""
                    WITH RECURSIVE scope AS (
                        SELECT place_id FROM places WHERE place_id = {placeId}
                        UNION
                        SELECT p.place_id FROM places p JOIN scope s ON p.parent_id = s.place_id
                    ) SELECT place_id AS "Value" FROM scope
                    """).ToListAsync(ct);
                var open = await db.AirAlerts
                    .Where(a => a.SourceAlertId.StartsWith(KeyPrefix)
                        && a.StartedAt <= o.ObservedAt && (a.EndedAt == null || a.EndedAt > o.ObservedAt)
                        && descendants.Contains(a.PlaceId))
                    .ToListAsync(ct);
                foreach (var a in open)
                {
                    a.EndedAt = o.ObservedAt;
                    a.EndRawMessageId = o.RawMessageId;
                    events.Add(new PulujEvent(PulujEventType.AlertChanged, a.AirAlertId, now));
                }
                if (open.Count > 0)
                {
                    logger.LogDebug("Text alert cancelled for place {PlaceId}: {Count} interval(s) closed", placeId, open.Count);
                }
            }
        }
    }
}
