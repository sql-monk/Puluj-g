using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;
using Puluj.Processing.Writers;

namespace Puluj.Processing.Incidents;

/// <summary>
/// The `incident-worker` subscription (plan §8.4, ADR-0010, P10): fact writer of incident observations (their `targets`
/// rows) and the owner of the incident aggregate through <see cref="IncidentStateWriter"/>. Locks: Store shared →
/// `incident:kind:{id}` exclusive for the fact's kind and every kind it confirms (sorted) — candidates are read under
/// them, so two replicas never both open an incident for the same event. One `incident.changed` per changed incident.
/// </summary>
public sealed class IncidentWriterHandler(
    IDbContextFactory<PulujDbContext> factory,
    IncidentStateWriter writer,
    IndexProvider indexes,
    INotifyPublisher notifier,
    PulujMetrics metrics,
    TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "incident-worker";

    public string SubscriptionId => Subscription;
    public string Producer
    {
        get => writer.Instance;
        set => writer.Instance = value;
    }

    private sealed record Facts(RawMessage Raw, List<(Target Row, Guid ObservationId, string Kind, DateTimeOffset EffectiveAt)> Incidents, List<Guid> Observations, List<int> KindIds, Guid? RunGeneration, string? RunState);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        await indexes.Ready.WaitAsync(ct);
        _ = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without payload");
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "observations.recorded without raw_message_id");
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found");
        var incidents = new List<(Target, Guid, string, DateTimeOffset)>();
        var observations = new List<Guid>();
        var kindIds = new SortedSet<int>();
        foreach (var fact in WriterSupport.Facts(envelope))
        {
            var target = TargetMaterializer.FromFact(fact, rawId, raw.SourceId);
            if (target.ObservationId is { } anyId)
            {
                observations.Add(anyId); // whole event: the "another set" guard compares whole sets (P09)
            }
            if (TargetMaterializer.Category(fact) != "incident" || target.ObservationId is not { } observationId)
            {
                continue;
            }
            var kind = fact["event_kind_code"]?.GetValue<string>() ?? throw new PermanentDeliveryException("invalid_payload", "incident fact without event_kind_code");
            if (indexes.EventKinds.ByCode(kind) is null)
            {
                await indexes.RefreshAsync(ct); // a kind published after the last 10-minute refresh (review N9)
                if (indexes.EventKinds.ByCode(kind) is null)
                {
                    throw new PermanentDeliveryException("unknown_kind", $"event kind {kind} is not in the catalog");
                }
            }
            foreach (var id in writer.CandidateKindIds(kind))
            {
                kindIds.Add(id);
            }
            incidents.Add((target, observationId, kind, target.ObservedAt));
        }
        Guid? generation = null;
        string? runState = null;
        if (incidents.Count > 0)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ProcessingRuns.AsNoTracking().Where(r => r.RunId == envelope.ProcessingRunId).Select(r => new { r.GenerationId, r.State }).SingleOrDefaultAsync(ct);
            generation = run?.GenerationId;
            runState = run?.State;
        }
        return new Facts(raw, incidents, observations, kindIds.ToList(), generation, runState);
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var f = (Facts)state!;
        if (f.Incidents.Count == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_no_facts");
            return DeliveryResult.Noop("no incident facts for the incident branch");
        }
        if (envelope.Lane == "replay")
        {
            return await ApplyShadowAsync(conn, tx, envelope, f, sw, ct);
        }
        var lockMs = await WriterSupport.LockStoreSharedAsync(conn, tx, ct);
        foreach (var kindId in f.KindIds)
        {
            lockMs += await WriterSupport.LockAsync(conn, tx, $"incident:kind:{kindId}", shared: false, ct);
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

        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        var now = clock.GetUtcNow();
        var generation = await IncidentStateWriter.EnsureGenerationAsync(conn, tx, f.RunGeneration, ct);
        var toInsert = f.Incidents.Where(i => !written.Contains(i.ObservationId)).Select(i => i.Row).ToList();
        if (toInsert.Count > 0)
        {
            db.Targets.AddRange(toInsert);
            await db.SaveChangesAsync(ct); // the same insert (and trigger) the legacy writer did
        }
        else
        {
            // The rows exist (a republished event with a new id): the incident links may still be missing — continue with the stored rows.
            var ids = f.Incidents.Select(i => i.ObservationId).ToList();
            var stored = await db.Targets.Where(t => t.ObservationId != null && ids.Contains(t.ObservationId.Value)).ToListAsync(ct);
            f = f with { Incidents = f.Incidents.Select(i => (stored.FirstOrDefault(t => t.ObservationId == i.ObservationId) ?? i.Row, i.ObservationId, i.Kind, i.EffectiveAt)).ToList() };
        }
        await WriterSupport.LinkObservationsAsync(conn, tx, rawId, ct);

        var outgoing = new List<Envelope>();
        var notify = toInsert.Select(t => new PulujEvent(PulujEventType.TargetCreated, t.TargetId, now)).ToList(); // the map's live feed until P11
        foreach (var t in toInsert)
        {
            metrics.TargetCreated(f.Raw.Source!.Code, t.IdentificationMethod.ToString());
        }
        var changed = 0;
        foreach (var (row, observationId, kind, effectiveAt) in f.Incidents.OrderBy(i => i.EffectiveAt))
        {
            var change = await writer.RecordObservationAsync(db, new IncidentStateWriter.ObservationInput(row, observationId, kind, effectiveAt), generation, envelope.ProcessingRunId, envelope, ct);
            if (change is null)
            {
                continue; // already linked (a redelivery with another event id)
            }
            outgoing.Add(change.Event);
            changed++;
            metrics.WriterOutcome(Subscription, "incident_" + change.Change);
        }
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        if (changed == 0 && toInsert.Count == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_already_written");
            return DeliveryResult.Noop("already_written: the incident facts of this event are materialized and linked");
        }
        metrics.WriterOutcome(Subscription, "completed");
        return new DeliveryResult("completed", null, outgoing) { AfterCommit = token => NotifyAsync(notify, token) };
    }

    /// <summary>
    /// Shadow replay (P14, ADR-0005): the facts of a replay run are written **only** as incidents of the run's own generation —
    /// no `targets` row (the public map and its triggers never see them), no `legacy_target_id` link, no `TargetCreated` NOTIFY,
    /// none of the live ownership guards (live's rows for the same raw are another generation's evidence). Idempotency per generation:
    /// a raw already linked in this generation (a repeated batch, a rewound checkpoint) is a noop;
    /// a cancelled or rolled-back run is a noop too (its in-flight deliveries drain without effect).
    /// </summary>
    private async Task<DeliveryResult> ApplyShadowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Facts f, Stopwatch sw, CancellationToken ct)
    {
        if (f.RunGeneration is not { } generation)
        {
            throw new PermanentDeliveryException("invalid_run", $"replay delivery of run {envelope.ProcessingRunId} without a generation");
        }
        if (f.RunState is "cancelled" or "rolled_back" or "failed")
        {
            metrics.WriterOutcome(Subscription, "noop_run_" + f.RunState);
            return DeliveryResult.Noop($"run_{f.RunState}: the replay run is {f.RunState}; its deliveries drain without effect");
        }
        var lockMs = 0L;
        foreach (var kindId in f.KindIds)
        {
            lockMs += await WriterSupport.LockAsync(conn, tx, $"incident:kind:{kindId}", shared: false, ct);
        }
        metrics.WriterStage(Subscription, "lock_wait", lockMs);
        await using (var linked = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM incident_observations io JOIN processing.observations o ON o.observation_id = io.observation_id WHERE io.generation_id = @g AND o.raw_message_id = @raw)", conn, tx))
        {
            linked.Parameters.AddWithValue("g", generation);
            linked.Parameters.AddWithValue("raw", f.Raw.RawMessageId);
            if ((bool)(await linked.ExecuteScalarAsync(ct))!)
            {
                metrics.WriterOutcome(Subscription, "noop_already_written");
                return DeliveryResult.Noop("already_written: this raw message is already linked in the run's generation");
            }
        }
        await using var db = await ConsumerDbContext.AttachAsync(conn, tx, ct);
        await IncidentStateWriter.EnsureGenerationAsync(conn, tx, generation, ct);
        var outgoing = new List<Envelope>();
        var changed = 0;
        foreach (var (row, observationId, kind, effectiveAt) in f.Incidents.OrderBy(i => i.EffectiveAt))
        {
            var change = await writer.RecordObservationAsync(db, new IncidentStateWriter.ObservationInput(row, observationId, kind, effectiveAt), generation, envelope.ProcessingRunId, envelope, ct);
            if (change is null)
            {
                continue;
            }
            outgoing.Add(change.Event); // `incident.changed` in the replay lane: archived, never projected (topology v9)
            changed++;
            metrics.WriterOutcome(Subscription, "shadow_incident_" + change.Change);
        }
        metrics.WriterStage(Subscription, "apply", sw.ElapsedMilliseconds - lockMs);
        if (changed == 0)
        {
            metrics.WriterOutcome(Subscription, "noop_already_written");
            return DeliveryResult.Noop("already_written: the incident facts of this event are linked in the generation");
        }
        metrics.WriterOutcome(Subscription, "completed");
        return new DeliveryResult("completed", null, outgoing);
    }

    private async Task NotifyAsync(IEnumerable<PulujEvent> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            await notifier.PublishAsync(e, ct);
        }
    }
}
