using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Correlation;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;

namespace Puluj.Processing.Writers;

/// <summary>
/// The `track-worker` subscription (plan §7, ADR-0009, P09): the fact writer of every non-alert observation (the
/// `targets` compatibility projection, one row per observation) and the owner of the track aggregate. Correlation is
/// the legacy <see cref="CorrelationSink"/> run inside the delivery transaction under the lock hierarchy
/// Store(shared) → track(shared | exclusive) → category(exclusive, sorted): candidate tracks are read under the lock, so
/// two replicas cannot both decide "no candidate" (candidate-create race). Every changed track gets revision + 1 and a
/// `track.changed`; `track.expiry.requested` closes a track only when its revision still matches.
/// </summary>
public sealed class TrackWriterHandler(
    IDbContextFactory<PulujDbContext> factory,
    CorrelationSink correlation,
    IndexProvider indexes,
    INotifyPublisher notifier,
    IOptionsMonitor<CorrelationOptions> options,
    PulujMetrics metrics,
    TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "track-worker";
    public const string EventType = "track.changed";
    public const string ExpiryCommand = "track.expiry.requested";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Facts(long RawId, Source Source, List<Target> Targets, List<(Target Fact, string Reason)> Cancellations, List<Guid> Observations, bool ExclusiveTrack, List<int> Categories);
    private sealed record Expiry(long TrackId, int ExpectedRevision, DateTimeOffset Watermark, string Reason, int? Category);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        await indexes.Ready.WaitAsync(ct);
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without payload");
        if (envelope.EventType == ExpiryCommand)
        {
            var trackId = payload["track_id"]?.GetValue<long>() ?? throw new PermanentDeliveryException("invalid_payload", "track.expiry.requested without track_id");
            int? category;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                category = await db.TargetTracks.AsNoTracking().Where(t => t.TargetTrackId == trackId).Select(t => (int?)t.TargetCategoryId).SingleOrDefaultAsync(ct);
            }
            return new Expiry(trackId,
                payload["expected_revision"]?.GetValue<int>() ?? 0,
                Parse(payload["watermark"]),
                payload["reason"]?.GetValue<string>() ?? "timeout",
                category);
        }
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "observations.recorded without raw_message_id");
        // Raw/source are read here, not inside the transaction (the FK of the inserted rows still takes KEY SHARE on the raw row: a
        // legacy processor or a reset holding it while waiting for Store is a deadlock the server resolves — the consumer requeues, ADR-0009 §7).
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found");
        var targets = new List<Target>();
        var cancellations = new List<(Target, string)>();
        var observations = new List<Guid>();
        foreach (var fact in WriterSupport.Facts(envelope))
        {
            var category = TargetMaterializer.Category(fact);
            var target = TargetMaterializer.FromFact(fact, rawId, raw.SourceId);
            if (target.ObservationId is { } anyId)
            {
                observations.Add(anyId); // every observation of the event, both branches: the "another set" guard compares whole sets
            }
            // Cancellations by the legacy enum, exactly what the sinks key on (a new rule code with the same legacy meaning behaves the same).
            if (category == "alert")
            {
                if (target.EventType == Domain.Enums.EventType.AlertCancelled)
                {
                    cancellations.Add((target, "alert_cancelled")); // the alert-worker owns the row; the track owner only reacts
                }
                continue;
            }
            targets.Add(target); // fact writer for target / incident / info (review B4)
            if (target.EventType == Domain.Enums.EventType.TargetCancelled)
            {
                cancellations.Add((target, "target_cancelled"));
            }
        }
        // Lock set decided up front (review N6): a cancellation without a category needs the whole track scope.
        var exclusive = cancellations.Any(c => c.Item1.TargetCategoryId is null);
        var categories = targets.Where(t => t.TargetCategoryId is not null).Select(t => t.TargetCategoryId!.Value)
            .Concat(cancellations.Where(c => c.Item1.TargetCategoryId is not null).Select(c => c.Item1.TargetCategoryId!.Value))
            .Distinct().Order().ToList();
        return new Facts(rawId, raw.Source!, targets, cancellations, observations, exclusive, categories);
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
        if (f.Targets.Count == 0 && f.Cancellations.Count == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_no_facts");
            return DeliveryResult.Noop("no target facts for the track branch");
        }
        lockMs += await WriterSupport.LockAsync(conn, tx, WriterSupport.TrackScope, shared: !f.ExclusiveTrack, ct);
        if (!f.ExclusiveTrack)
        {
            foreach (var category in f.Categories)
            {
                lockMs += await WriterSupport.LockAsync(conn, tx, $"{WriterSupport.TrackScope}:cat:{category}", shared: false, ct);
            }
        }
        metrics.WriterStage(Subscription, "lock_wait", lockMs);

        // Double-writer guards (ADR-0009): the legacy loop already wrote this raw, or another observation set did (another run).
        if (await WriterSupport.LegacyOwnedAsync(conn, tx, f.RawId, ct))
        {
            metrics.WriterOutcome(Subscription, "noop_legacy_owned");
            return DeliveryResult.Noop("legacy_owned: the legacy processor wrote this raw message");
        }
        var written = await WriterSupport.WrittenObservationsAsync(conn, tx, f.RawId, ct);
        if (written.Count > 0 && !written.Overlaps(f.Observations))
        {
            metrics.WriterOutcome(Subscription, "noop_already_written");
            return DeliveryResult.Noop("already_written: another observation set of this raw is materialized");
        }
        var toInsert = f.Targets.Where(t => t.ObservationId is null || !written.Contains(t.ObservationId.Value)).ToList();

        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        var now = clock.GetUtcNow();
        var events = new List<PulujEvent>();
        var tracksBefore = await db.TargetTracks.MaxAsync(t => (long?)t.TargetTrackId, ct) ?? 0; // tracks above this id were opened by this delivery
        if (toInsert.Count > 0)
        {
            db.Targets.AddRange(toInsert);
            await db.SaveChangesAsync(ct); // ids for the sinks; the insert trigger (stats, anchors, kinematic links) fires here, as it did for the legacy writer
            foreach (var t in toInsert)
            {
                events.Add(new PulujEvent(PulujEventType.TargetCreated, t.TargetId, now)); // the map's live feed (NotifyBridge) until the projection consumer (P11)
                metrics.TargetCreated(f.Source.Code, t.IdentificationMethod.ToString());
            }
        }
        // Rows of this delivery that were already there (a republished event) still take part in the correlation only once: skip them.
        await correlation.HandleTargetFactsAsync(db, toInsert, f.Source, events, now, ct);
        foreach (var (cancel, reason) in f.Cancellations)
        {
            await correlation.CloseTracksForCancellationAsync(db, cancel, reason, events, now, ct);
        }
        await db.SaveChangesAsync(ct);
        await WriterSupport.LinkObservationsAsync(conn, tx, f.RawId, ct);

        // One revision and one event per changed track, whatever the sinks touched.
        var outgoing = new List<Envelope>();
        var changed = events.Where(e => e.Type is PulujEventType.TrackUpserted or PulujEventType.TrackClosed).GroupBy(e => e.Id)
            .Select(g => (Id: g.Key, Closed: g.Any(e => e.Type == PulujEventType.TrackClosed))).ToList();
        foreach (var (trackId, closed) in changed)
        {
            var eventId = Guid.CreateVersion7();
            var stateAfter = await WriterSupport.BumpTrackAsync(conn, tx, trackId, eventId, envelope.CorrelationId, ct);
            // `created` = opened by this delivery (a duplicate attached to a legacy track is an update); `closed` is reserved for a future non-cancel closure.
            var change = closed
                ? stateAfter.Status == "cancelled" ? "cancelled" : "closed"
                : trackId > tracksBefore ? "created" : "updated";
            var observationIds = closed
                ? f.Cancellations.Select(c => c.Fact.ObservationId).OfType<Guid>().ToList()
                : await WriterSupport.TrackObservationsAsync(conn, tx, trackId, f.RawId, ct);
            outgoing.Add(TrackChanged(envelope, eventId, stateAfter, change, observationIds, now, closed ? f.Cancellations.FirstOrDefault().Reason : null));
        }
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        metrics.WriterOutcome(Subscription, "completed");
        return new DeliveryResult("completed", null, outgoing) { AfterCommit = token => NotifyAsync(events, token) };
    }

    private async Task<DeliveryResult> ExpireAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Expiry e, long lockMs, Stopwatch sw, CancellationToken ct)
    {
        if (e.Category is int category)
        {
            lockMs += await WriterSupport.LockAsync(conn, tx, WriterSupport.TrackScope, shared: true, ct);
            lockMs += await WriterSupport.LockAsync(conn, tx, $"{WriterSupport.TrackScope}:cat:{category}", shared: false, ct);
        }
        metrics.WriterStage(Subscription, "lock_wait", lockMs);
        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        var track = await db.TargetTracks.SingleOrDefaultAsync(t => t.TargetTrackId == e.TrackId, ct);
        if (track is null)
        {
            metrics.WriterOutcome(Subscription, "noop_unknown_track");
            return DeliveryResult.Noop($"track {e.TrackId} does not exist");
        }
        if (track.Revision != e.ExpectedRevision)
        {
            metrics.WriterOutcome(Subscription, "noop_stale_revision");
            return DeliveryResult.Noop($"stale_revision: track {e.TrackId} is at {track.Revision}, command expected {e.ExpectedRevision}");
        }
        if (track.Status != TrackStatus.Active)
        {
            metrics.WriterOutcome(Subscription, "noop_not_active");
            return DeliveryResult.Noop($"track {e.TrackId} is {track.Status}");
        }
        var window = indexes.Taxonomy.ClassProfile(track.TargetClassId)?.CorrelationWindowMinutes ?? 30;
        var closeAt = track.LastSeenAt + TimeSpan.FromMinutes(window * options.CurrentValue.CloseAfterWindows);
        if (closeAt > e.Watermark)
        {
            metrics.WriterOutcome(Subscription, "noop_still_fresh");
            return DeliveryResult.Noop($"still_fresh: track {e.TrackId} closes at {closeAt:O}, watermark {e.Watermark:O}");
        }
        track.Status = TrackStatus.Closed;
        track.ClosedReason = "timeout";
        track.UpdatedAt = closeAt > track.UpdatedAt ? closeAt : track.UpdatedAt; // event time, never the clock (legacy watchdog semantics)
        db.TargetTrackRevisions.Add(TrackUpdater.Revision(track, null, track.UpdatedAt));
        await db.SaveChangesAsync(ct);
        var eventId = Guid.CreateVersion7();
        var after = await WriterSupport.BumpTrackAsync(conn, tx, track.TargetTrackId, eventId, envelope.CorrelationId, ct);
        var now = clock.GetUtcNow();
        var changed = TrackChanged(envelope, eventId, after, "expired", [], now, e.Reason);
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        metrics.WriterOutcome(Subscription, "expired");
        var notify = new List<PulujEvent> { new(PulujEventType.TrackClosed, track.TargetTrackId, now) };
        return new DeliveryResult("completed", "expired", [changed]) { AfterCommit = token => NotifyAsync(notify, token) };
    }

    private Envelope TrackChanged(Envelope cause, Guid eventId, WriterSupport.TrackState s, string change, IReadOnlyList<Guid> observations, DateTimeOffset recordedAt, string? reason)
    {
        var category = indexes.Taxonomy.CategoryCode(s.TargetCategoryId); // the taxonomy code (uav, missile…), stable for consumers
        var payload = new JsonObject
        {
            ["track_id"] = s.TrackId,
            ["change"] = change,
            ["revision"] = s.Revision,
            ["effective_at"] = FactMapper.Iso(s.UpdatedAt),
            ["recorded_at"] = FactMapper.Iso(recordedAt),
            ["observation_ids"] = WriterSupport.Ids(observations),
            ["category"] = category,
            ["status"] = s.Status,
        };
        if (reason is not null)
        {
            payload["reason"] = reason;
        }
        // occurred_at = the aggregate's effective time (business time, envelope §5.1); recorded_at in the payload is the clock.
        return WriterSupport.AggregateEvent(cause, EventType, Producer, s.UpdatedAt, $"track:{s.TrackId}", s.Revision, $"track:cat:{category}", payload, eventId);
    }

    /// <summary>The legacy live channel (API/SignalR: targets feed, track upserts/closures) until the projection consumer (P11): best effort after the commit.</summary>
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
