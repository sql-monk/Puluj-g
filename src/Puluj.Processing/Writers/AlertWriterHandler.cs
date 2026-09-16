using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;
using Puluj.Processing.Structured;

namespace Puluj.Processing.Writers;

/// <summary>
/// The `alert-worker` subscription (plan §7, ADR-0009, P09): owner of the alert-interval aggregate (`air_alerts`) and
/// fact writer of alert observations. Structured alerts reuse <see cref="AlertsInUaHandler"/> (external alert identity,
/// start/end in any order); text alerts reuse <see cref="TextAlertSink"/> (interval per place, cancellations cascade to
/// child places). Scope lock = the region of the alerted place (Store shared → `alert:region:{id}` sorted), so a text
/// cancellation of an oblast and the alerts of its raions serialize. Revision + 1 and `alert.changed` per changed
/// interval; `alert.expiry.requested` expires a text alert only when its revision still matches.
/// </summary>
public sealed class AlertWriterHandler(
    IDbContextFactory<PulujDbContext> factory,
    AlertsInUaHandler structured,
    TextAlertSink textAlerts,
    IndexProvider indexes,
    INotifyPublisher notifier,
    PulujMetrics metrics,
    TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "alert-worker";
    public const string EventType = "alert.changed";
    public const string ExpiryCommand = "alert.expiry.requested";
    public const string UnknownScope = "alert:unknown";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Facts(RawMessage Raw, List<Target> Targets, List<Guid> Observations, List<string> Scopes, bool Structured);
    private sealed record Expiry(long AlertId, int ExpectedRevision, DateTimeOffset Watermark, string Reason, string Scope);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        await indexes.Ready.WaitAsync(ct);
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without payload");
        if (envelope.EventType == ExpiryCommand)
        {
            var alertId = payload["alert_id"]?.GetValue<long>() ?? throw new PermanentDeliveryException("invalid_payload", "alert.expiry.requested without alert_id");
            int? placeId;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                placeId = await db.AirAlerts.AsNoTracking().Where(a => a.AirAlertId == alertId).Select(a => (int?)a.PlaceId).SingleOrDefaultAsync(ct);
            }
            return new Expiry(alertId, payload["expected_revision"]?.GetValue<int>() ?? 0, Parse(payload["watermark"]), payload["reason"]?.GetValue<string>() ?? "max_age", Scope(placeId));
        }
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "observations.recorded without raw_message_id");
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found");
        var targets = new List<Target>();
        var observations = new List<Guid>();
        var isStructured = false;
        foreach (var fact in WriterSupport.Facts(envelope))
        {
            var target = TargetMaterializer.FromFact(fact, rawId, raw.SourceId);
            if (target.ObservationId is { } oid)
            {
                observations.Add(oid);
            }
            if (TargetMaterializer.Category(fact) != "alert")
            {
                continue;
            }
            targets.Add(target);
            isStructured |= target.IdentificationMethod == IdentificationMethod.Structured;
        }
        var scopes = targets.Select(t => Scope(t.LocationPlaceId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new Facts(raw, targets, observations, scopes, isStructured);
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var lockMs = await WriterSupport.LockStoreSharedAsync(conn, tx, ct);
        if (state is Expiry expiry)
        {
            return await ExpireAsync(conn, tx, envelope, expiry, lockMs, sw, ct);
        }
        var f = (Facts)state!;
        if (f.Targets.Count == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_no_facts");
            return DeliveryResult.Noop("no alert facts for the alert branch");
        }
        foreach (var scope in f.Scopes)
        {
            lockMs += await WriterSupport.LockAsync(conn, tx, scope, shared: false, ct);
        }
        metrics.WriterStage(Subscription, "lock_wait", lockMs);
        var rawId = f.Raw.RawMessageId;
        if (await WriterSupport.LegacyOwnedAsync(conn, tx, rawId, ct))
        {
            metrics.WriterOutcome(Subscription, "noop_legacy_owned");
            return DeliveryResult.Noop("legacy_owned: the legacy processor wrote this raw message");
        }
        var written = await WriterSupport.WrittenObservationsAsync(conn, tx, rawId, ct);
        if (written.Count > 0 && !written.Overlaps(f.Observations))
        {
            metrics.WriterOutcome(Subscription, "noop_already_written");
            return DeliveryResult.Noop("already_written: another observation set of this raw is materialized");
        }
        var pending = f.Targets.Where(t => t.ObservationId is null || !written.Contains(t.ObservationId.Value)).ToList();
        if (pending.Count == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_already_written");
            return DeliveryResult.Noop("already_written: the alert facts of this event are materialized");
        }

        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        var now = clock.GetUtcNow();
        var events = new List<PulujEvent>();
        // Pre-image of the intervals the sinks may touch (this transaction's view): what changed is decided by comparing
        // rows before and after, not by what the sinks announce (review B1: a re-announced level, a silent close past
        // MaxAge and a start filling in its message are changes too).
        var before = await db.AirAlerts.AsNoTracking().Where(a => a.EndedAt == null || a.StartedAt >= now.AddDays(-7))
            .Select(a => new { a.AirAlertId, a.EndedAt, a.Level, a.StartRawMessageId, a.EndRawMessageId }).ToDictionaryAsync(a => a.AirAlertId, ct);
        List<Target> rows;
        if (f.Structured)
        {
            // The same code the legacy processor ran: it reads/creates the interval by external id and returns the target row.
            rows = await structured.HandleAsync(db, f.Raw, f.Raw.Source!, ct);
            var fact = pending[0];
            foreach (var r in rows)
            {
                r.ObservationId = fact.ObservationId;
                r.EventKindId = fact.EventKindId;
            }
        }
        else
        {
            rows = pending;
        }
        db.Targets.AddRange(rows);
        await db.SaveChangesAsync(ct);
        if (!f.Structured)
        {
            await textAlerts.OnTargetsAsync(db, rows, f.Raw.Source!, events, ct);
        }
        await db.SaveChangesAsync(ct);
        await WriterSupport.LinkObservationsAsync(conn, tx, rawId, ct);

        // Post-image: every row that differs from the pre-image, plus every row that did not exist.
        var changes = new Dictionary<long, string>();
        var postImage = await db.AirAlerts.AsNoTracking().Where(a => a.EndedAt == null || a.StartedAt >= now.AddDays(-7) || a.StartRawMessageId == rawId || a.EndRawMessageId == rawId)
            .Select(a => new { a.AirAlertId, a.EndedAt, a.Level, a.StartRawMessageId, a.EndRawMessageId }).ToListAsync(ct);
        foreach (var a in postImage)
        {
            if (!before.TryGetValue(a.AirAlertId, out var b))
            {
                changes[a.AirAlertId] = "started";
            }
            else if (b.EndedAt is null && a.EndedAt is not null)
            {
                changes[a.AirAlertId] = "ended";
            }
            else if (b.Level != a.Level || b.EndedAt != a.EndedAt || b.StartRawMessageId != a.StartRawMessageId || b.EndRawMessageId != a.EndRawMessageId)
            {
                changes[a.AirAlertId] = "updated";
            }
        }
        foreach (var e in events.Where(e => e.Type == PulujEventType.AlertChanged))
        {
            changes.TryAdd(e.Id, "updated"); // announced by the sink but outside the pre-image window (very old history rows)
        }

        var outgoing = new List<Envelope>();
        var notify = rows.Select(r => new PulujEvent(PulujEventType.TargetCreated, r.TargetId, now)).ToList(); // the map's live feed until P11
        foreach (var r in rows)
        {
            metrics.TargetCreated(f.Raw.Source!.Code, r.IdentificationMethod.ToString());
        }
        foreach (var (alertId, change) in changes.OrderBy(kv => kv.Key))
        {
            var eventId = Guid.CreateVersion7();
            var after = await WriterSupport.BumpAlertAsync(conn, tx, alertId, eventId, envelope.CorrelationId, ct);
            outgoing.Add(AlertChanged(envelope, eventId, after, change, rows.Select(r => r.ObservationId).OfType<Guid>().ToList(), now, null));
            notify.Add(new PulujEvent(PulujEventType.AlertChanged, alertId, now));
        }
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        metrics.WriterOutcome(Subscription, outgoing.Count == 0 ? "completed_no_change" : "completed");
        return new DeliveryResult("completed", null, outgoing) { AfterCommit = token => NotifyAsync(notify, token) };
    }

    private async Task<DeliveryResult> ExpireAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Expiry e, long lockMs, Stopwatch sw, CancellationToken ct)
    {
        lockMs += await WriterSupport.LockAsync(conn, tx, e.Scope, shared: false, ct);
        metrics.WriterStage(Subscription, "lock_wait", lockMs);
        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        var alert = await db.AirAlerts.SingleOrDefaultAsync(a => a.AirAlertId == e.AlertId, ct);
        if (alert is null)
        {
            metrics.WriterOutcome(Subscription, "noop_unknown_alert");
            return DeliveryResult.Noop($"alert {e.AlertId} does not exist");
        }
        if (alert.Revision != e.ExpectedRevision)
        {
            metrics.WriterOutcome(Subscription, "noop_stale_revision");
            return DeliveryResult.Noop($"stale_revision: alert {e.AlertId} is at {alert.Revision}, command expected {e.ExpectedRevision}");
        }
        if (alert.EndedAt is not null)
        {
            metrics.WriterOutcome(Subscription, "noop_not_active");
            return DeliveryResult.Noop($"alert {e.AlertId} already ended");
        }
        if (!alert.SourceAlertId.StartsWith(TextAlertSink.KeyPrefix, StringComparison.Ordinal) || alert.StartedAt + TextAlertSink.MaxAge > e.Watermark)
        {
            metrics.WriterOutcome(Subscription, "noop_still_fresh");
            return DeliveryResult.Noop($"still_fresh: alert {e.AlertId} is not a text alert past its max age at watermark {e.Watermark:O}");
        }
        alert.EndedAt = alert.StartedAt + TextAlertSink.MaxAge;
        await db.SaveChangesAsync(ct);
        var eventId = Guid.CreateVersion7();
        var after = await WriterSupport.BumpAlertAsync(conn, tx, alert.AirAlertId, eventId, envelope.CorrelationId, ct);
        var now = clock.GetUtcNow();
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        metrics.WriterOutcome(Subscription, "expired");
        var notify = new List<PulujEvent> { new(PulujEventType.AlertChanged, alert.AirAlertId, now) };
        return new DeliveryResult("completed", "expired", [AlertChanged(envelope, eventId, after, "expired", [], now, e.Reason)]) { AfterCommit = token => NotifyAsync(notify, token) };
    }

    private Envelope AlertChanged(Envelope cause, Guid eventId, WriterSupport.AlertState s, string change, IReadOnlyList<Guid> observations, DateTimeOffset recordedAt, string? reason)
    {
        var scope = new JsonObject { ["place_id"] = s.PlaceId };
        if (!s.SourceAlertId.StartsWith(TextAlertSink.KeyPrefix, StringComparison.Ordinal))
        {
            scope["external_alert_id"] = s.SourceAlertId;
        }
        if (s.Level != (int)AirAlertLevel.Unknown)
        {
            scope["level"] = ((AirAlertLevel)s.Level).ToString().ToLowerInvariant();
        }
        var payload = new JsonObject
        {
            ["alert_id"] = s.AlertId,
            ["change"] = change,
            ["revision"] = s.Revision,
            ["effective_at"] = FactMapper.Iso(s.EndedAt ?? s.StartedAt),
            ["recorded_at"] = FactMapper.Iso(recordedAt),
            ["observation_ids"] = WriterSupport.Ids(observations),
            ["scope"] = scope,
            ["started_at"] = FactMapper.Iso(s.StartedAt),
        };
        if (s.EndedAt is { } ended)
        {
            payload["ended_at"] = FactMapper.Iso(ended);
        }
        if (reason is not null)
        {
            payload["reason"] = reason;
        }
        return WriterSupport.AggregateEvent(cause, EventType, Producer, s.EndedAt ?? s.StartedAt, $"alert:{s.AlertId}", s.Revision, Scope(s.PlaceId), payload, eventId);
    }

    /// <summary>Lock scope of an alert: the region of its place (an oblast cancellation and its raions serialize), `alert:unknown` without one.</summary>
    private string Scope(int? placeId)
    {
        if (placeId is not int id || indexes.Gazetteer.Get(id) is not { } place)
        {
            return UnknownScope;
        }
        var region = indexes.Gazetteer.RegionOf(place) ?? place;
        return $"alert:region:{region.PlaceId}";
    }

    private async Task NotifyAsync(IEnumerable<PulujEvent> events, CancellationToken ct)
    {
        foreach (var e in events.DistinctBy(e => (e.Type, e.Id)))
        {
            await notifier.PublishAsync(e, ct);
        }
    }

    private static DateTimeOffset Parse(JsonNode? node) =>
        DateTimeOffset.TryParse(node?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : throw new PermanentDeliveryException("invalid_payload", "expiry command without a valid timestamp");
}
