using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Correlation;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;
using Puluj.Processing.Writers;

namespace Puluj.Processing.Incidents;

/// <summary>The request cannot be applied to the incident in its current state (wrong state, different kinds, unknown observation).</summary>
public sealed class IncidentConflictException(string message) : Exception(message);

public sealed class IncidentNotFoundException(long id) : Exception($"incident {id} does not exist");

/// <summary>What one change did to one incident: the row after it, the revision and the event to publish.</summary>
public sealed record IncidentChange(Incident Incident, string Change, IReadOnlyList<Guid> ObservationIds, Envelope Event);

/// <summary>
/// Plan §8.4 (P10): the only writer of incidents. The worker feeds observations through <see cref="RecordObservationAsync"/>
/// inside its delivery transaction; the admin commands (resolve, retract, confirm, suppress, merge, split) go through the
/// same class in a transaction of their own, under the same locks (Store shared → `incident:kind:{id}` sorted), with the
/// same revision rows and `incident.changed` events. Raw evidence (`targets`, `processing.observations`) is never edited here.
/// </summary>
public sealed class IncidentStateWriter(
    IDbContextFactory<PulujDbContext> factory,
    OutboxWriter outbox,
    IndexProvider indexes,
    TimeProvider clock,
    ILogger<IncidentStateWriter> logger)
{
    public const string EventType = "incident.changed";
    public const string Producer = "incident-worker";
    public static readonly Guid Namespace = new("0f7d3c2a-5b61-4e0c-9a8e-7c1d2b3e4f50");
    /// <summary>The result set of the live pipeline until P14 generations: one deterministic id, created on demand.</summary>
    public static readonly Guid LiveGeneration = SourceIdentity.NameBasedGuid(Namespace, "generation:live");

    public string Instance { get; set; } = Producer;

    // ---- worker path ----

    public sealed record ObservationInput(Target Row, Guid ObservationId, string KindCode, DateTimeOffset EffectiveAt);

    /// <summary>Kind ids whose incidents a fact of <paramref name="kindCode"/> may join: its own and the kinds it confirms — the lock set (ADR-0010).</summary>
    public IReadOnlyList<int> CandidateKindIds(string kindCode)
    {
        var kinds = indexes.EventKinds;
        var own = kinds.ByCode(kindCode);
        if (own is null)
        {
            return [];
        }
        var ids = new List<int> { own.EventKindId };
        foreach (var confirmed in KindPolicy.From(own).Confirms)
        {
            if (kinds.ByCode(confirmed) is { } k)
            {
                ids.Add(k.EventKindId);
            }
        }
        return ids.Distinct().Order().ToList();
    }

    /// <summary>Ensures the generation the rows are stamped with exists (`live` until P14 promotes real generations).</summary>
    public static async Task<Guid> EnsureGenerationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? runGeneration, CancellationToken ct)
    {
        var generation = runGeneration ?? LiveGeneration;
        await using var cmd = new NpgsqlCommand("INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES (@id, NOT EXISTS (SELECT 1 FROM processing.generations WHERE is_active), now()) ON CONFLICT (generation_id) DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue("id", generation);
        await cmd.ExecuteNonQueryAsync(ct);
        return generation;
    }

    /// <summary>
    /// Links one observation (its `targets` row already saved in <paramref name="db"/>) to an incident by policy, or opens one.
    /// Caller holds the kind locks. Idempotent: an observation already linked is a noop (null).
    /// </summary>
    public async Task<IncidentChange?> RecordObservationAsync(PulujDbContext db, ObservationInput input, Guid generationId, Guid? runId, Envelope cause, CancellationToken ct)
    {
        if (await db.IncidentObservations.AnyAsync(l => l.ObservationId == input.ObservationId, ct))
        {
            return null;
        }
        var kinds = indexes.EventKinds;
        var kind = kinds.ByCode(input.KindCode) ?? throw new IncidentConflictException($"unknown event kind {input.KindCode}");
        var policy = KindPolicy.From(kind);
        var candidateKindIds = CandidateKindIds(input.KindCode);
        var gazetteer = indexes.Gazetteer;
        var row = input.Row;
        var now = clock.GetUtcNow();
        var fact = new IncidentFact(input.ObservationId, row.SourceId, input.KindCode, input.EffectiveAt, Correlator.AnchorOf(row, gazetteer), row.LocationPlaceId, RegionOf(row.LocationPlaceId));

        var window = TimeSpan.FromMinutes(policy.WindowMinutes);
        var rows = await db.Incidents.Include(i => i.Observations)
            .Where(i => i.GenerationId == generationId && candidateKindIds.Contains(i.EventKindId) && i.EventAt >= input.EffectiveAt - window && i.EventAt <= input.EffectiveAt + window)
            .ToListAsync(ct);
        var candidates = rows.Select(i => new IncidentCandidate(i.IncidentId, kinds.ByCode(KindCode(i.EventKindId))?.Code ?? "", i.State, i.Suppressed, i.EventAt,
                i.State is Incident.Resolved or Incident.Retracted ? i.UpdatedAt : null, AnchorOf(i, gazetteer), i.LocationPlaceId, RegionOf(i.LocationPlaceId),
                i.Observations.Select(o => o.SourceId).ToHashSet(), i.Observations.FirstOrDefault(o => o.Relation == IncidentObservation.Canonical)?.SourceId))
            .ToList();
        var decision = IncidentPolicy.Decide(fact, candidates, policy);

        Incident incident;
        string change;
        if (decision.CreatesIncident)
        {
            incident = new Incident
            {
                GenerationId = generationId,
                RunId = runId,
                EventKindId = kind.EventKindId,
                State = Incident.Reported,
                FirstReportedAt = input.EffectiveAt,
                LastReportedAt = input.EffectiveAt,
                EventAt = input.EffectiveAt,
                LocationKind = row.LocationKind,
                LocationPlaceId = row.LocationPlaceId,
                Geometry = row.Location,
                AccuracyKm = row.LocationAccuracyKm,
                Confidence = row.Confidence,
                SourceCount = 1,
                CanonicalObservationId = input.ObservationId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Incidents.Add(incident);
            change = "created";
        }
        else
        {
            incident = rows.Single(i => i.IncidentId == decision.IncidentId);
            incident.EventAt = incident.EventAt <= input.EffectiveAt ? incident.EventAt : input.EffectiveAt; // the earliest evidence
            incident.FirstReportedAt = incident.FirstReportedAt <= input.EffectiveAt ? incident.FirstReportedAt : input.EffectiveAt;
            incident.LastReportedAt = incident.LastReportedAt >= input.EffectiveAt ? incident.LastReportedAt : input.EffectiveAt;
            if (decision.Relation != IncidentObservation.Echo && !incident.Observations.Any(o => o.SourceId == row.SourceId))
            {
                incident.SourceCount++;
            }
            if (row.Confidence > incident.Confidence)
            {
                incident.Confidence = row.Confidence;
            }
            // Location: the most precise evidence wins; a coarser report never widens it (§8.5).
            if (row.Location is not null && (incident.Geometry is null || (row.LocationAccuracyKm ?? double.MaxValue) < (incident.AccuracyKm ?? double.MaxValue)))
            {
                incident.Geometry = row.Location;
                incident.AccuracyKm = row.LocationAccuracyKm;
                incident.LocationKind = row.LocationKind;
                incident.LocationPlaceId = row.LocationPlaceId;
            }
            incident.State = IncidentPolicy.NextState(incident.State, decision.Relation);
            incident.UpdatedAt = now;
            change = "updated";
        }
        var link = new IncidentObservation
        {
            Incident = incident,
            ObservationId = input.ObservationId,
            GenerationId = generationId,
            LegacyTargetId = row.TargetId,
            SourceId = row.SourceId,
            Relation = decision.Relation,
            Score = decision.Score,
            DecisionReason = JsonDocument.Parse(decision.Reason.ToJsonString()),
            PolicyVersion = $"{IncidentPolicy.Version}/p{kinds.PolicyVersion}",
            EffectiveAt = input.EffectiveAt,
            LinkedAt = now,
        };
        db.IncidentObservations.Add(link);
        incident.Observations.Add(link);
        await db.SaveChangesAsync(ct);
        return await ReviseAsync(db, incident, change, [input.ObservationId], cause, Instance, decision.Relation == IncidentObservation.Ambiguous ? "ambiguous candidates: separate incident for review" : null, input.EffectiveAt, now, ct);
    }

    // ---- admin commands (each in its own transaction, same locks, same revisions/events) ----

    public Task<IncidentChange> ResolveAsync(long id, string actor, string reason, DateTimeOffset? effectiveAt, CancellationToken ct) =>
        TransitionAsync(id, actor, reason, effectiveAt, "resolved", Incident.Resolved, "resolved", [Incident.Reported, Incident.Confirmed], ct);

    public Task<IncidentChange> RetractAsync(long id, string actor, string reason, DateTimeOffset? effectiveAt, CancellationToken ct) =>
        TransitionAsync(id, actor, reason, effectiveAt, "retracted", Incident.Retracted, "retracted", [Incident.Reported, Incident.Confirmed], ct);

    /// <summary>Manual confirmation: an operator with evidence outside the feed; the reason is the evidence.</summary>
    public Task<IncidentChange> ConfirmAsync(long id, string actor, string reason, DateTimeOffset? effectiveAt, CancellationToken ct) =>
        TransitionAsync(id, actor, reason, effectiveAt, "updated", Incident.Confirmed, null, [Incident.Reported], ct);

    public async Task<IncidentChange> SuppressAsync(long id, bool suppressed, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        var changes = await CommandAsync([id], async (db, incidents) =>
        {
            var incident = incidents[0];
            if (incident.Suppressed == suppressed)
            {
                throw new IncidentConflictException($"incident {id} is already {(suppressed ? "suppressed" : "visible")}");
            }
            incident.Suppressed = suppressed;
            incident.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return [(incident, suppressed ? "suppressed" : "updated", (IReadOnlyList<Guid>)[], (string?)null)];
        }, actor, reason, null, ct);
        return changes[0];
    }

    /// <summary>Moves every evidence link of <paramref name="sourceId"/> to <paramref name="targetId"/> (same kind); the source is retracted with reason `merged`.</summary>
    public async Task<IReadOnlyList<IncidentChange>> MergeAsync(long sourceId, long targetId, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        if (sourceId == targetId)
        {
            throw new IncidentConflictException("an incident cannot be merged into itself");
        }
        return await CommandAsync([sourceId, targetId], async (db, incidents) =>
        {
            var source = incidents.Single(i => i.IncidentId == sourceId);
            var target = incidents.Single(i => i.IncidentId == targetId);
            if (source.EventKindId != target.EventKindId)
            {
                throw new IncidentConflictException("incidents of different kinds are never merged (crossover, ADR-0010)");
            }
            if (source.State == Incident.Retracted || target.State == Incident.Retracted)
            {
                throw new IncidentConflictException("a retracted incident cannot take part in a merge");
            }
            var now = clock.GetUtcNow();
            var moved = source.Observations.Select(o => o.ObservationId).ToList();
            foreach (var link in source.Observations.ToList())
            {
                db.IncidentObservations.Remove(link);
            }
            await db.SaveChangesAsync(ct); // the unique (observation_id) frees before the re-insert
            foreach (var link in source.Observations)
            {
                var reasonJson = link.DecisionReason is null ? new JsonObject() : JsonNode.Parse(link.DecisionReason.RootElement.GetRawText())!.AsObject();
                reasonJson["merged_from"] = sourceId;
                reasonJson["merged_by"] = actor;
                db.IncidentObservations.Add(new IncidentObservation
                {
                    IncidentId = targetId,
                    ObservationId = link.ObservationId,
                    GenerationId = link.GenerationId,
                    LegacyTargetId = link.LegacyTargetId,
                    SourceId = link.SourceId,
                    Relation = IncidentObservation.Moved,
                    Score = link.Score,
                    DecisionReason = JsonDocument.Parse(reasonJson.ToJsonString()),
                    PolicyVersion = link.PolicyVersion,
                    EffectiveAt = link.EffectiveAt,
                    LinkedAt = now,
                });
            }
            source.Observations.Clear();
            source.State = Incident.Retracted;
            source.ClosureReason = "merged";
            source.MergedIntoIncidentId = targetId;
            source.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await db.Entry(target).Collection(t => t.Observations).LoadAsync(ct);
            target.SourceCount = target.Observations.Select(o => o.SourceId).Distinct().Count();
            target.FirstReportedAt = target.Observations.Min(o => o.EffectiveAt);
            target.LastReportedAt = target.Observations.Max(o => o.EffectiveAt);
            target.EventAt = target.FirstReportedAt;
            target.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return [(source, "merged", moved, "merged into " + targetId), (target, "updated", moved, "merged from " + sourceId)];
        }, actor, reason, null, ct);
    }

    /// <summary>Takes the given observations out of an incident into a new one of the same kind (same generation).</summary>
    public async Task<IReadOnlyList<IncidentChange>> SplitAsync(long id, IReadOnlyList<Guid> observationIds, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        if (observationIds.Count == 0)
        {
            throw new IncidentConflictException("split needs at least one observation");
        }
        return await CommandAsync([id], async (db, incidents) =>
        {
            var source = incidents[0];
            var links = source.Observations.Where(o => observationIds.Contains(o.ObservationId)).ToList();
            if (links.Count != observationIds.Count)
            {
                throw new IncidentConflictException("some observations do not belong to this incident");
            }
            if (links.Count == source.Observations.Count)
            {
                throw new IncidentConflictException("splitting away every observation would leave the incident empty; retract it instead");
            }
            var now = clock.GetUtcNow();
            var first = links.OrderBy(l => l.EffectiveAt).First();
            var target = await db.Targets.AsNoTracking().FirstOrDefaultAsync(t => t.TargetId == first.LegacyTargetId, ct);
            var split = new Incident
            {
                GenerationId = source.GenerationId,
                RunId = source.RunId,
                EventKindId = source.EventKindId,
                State = Incident.Reported,
                FirstReportedAt = links.Min(l => l.EffectiveAt),
                LastReportedAt = links.Max(l => l.EffectiveAt),
                EventAt = links.Min(l => l.EffectiveAt),
                LocationKind = target?.LocationKind ?? LocationKind.Unknown,
                LocationPlaceId = target?.LocationPlaceId,
                Geometry = target?.Location,
                AccuracyKm = target?.LocationAccuracyKm,
                Confidence = target?.Confidence ?? ConfidenceLevel.Unknown,
                SourceCount = links.Select(l => l.SourceId).Distinct().Count(),
                CanonicalObservationId = first.ObservationId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Incidents.Add(split);
            foreach (var link in links)
            {
                db.IncidentObservations.Remove(link);
                source.Observations.Remove(link);
            }
            await db.SaveChangesAsync(ct);
            foreach (var link in links)
            {
                var reasonJson = link.DecisionReason is null ? new JsonObject() : JsonNode.Parse(link.DecisionReason.RootElement.GetRawText())!.AsObject();
                reasonJson["split_from"] = id;
                reasonJson["split_by"] = actor;
                var moved = new IncidentObservation
                {
                    Incident = split,
                    ObservationId = link.ObservationId,
                    GenerationId = link.GenerationId,
                    LegacyTargetId = link.LegacyTargetId,
                    SourceId = link.SourceId,
                    Relation = link.ObservationId == first.ObservationId ? IncidentObservation.Canonical : IncidentObservation.Moved,
                    Score = link.Score,
                    DecisionReason = JsonDocument.Parse(reasonJson.ToJsonString()),
                    PolicyVersion = link.PolicyVersion,
                    EffectiveAt = link.EffectiveAt,
                    LinkedAt = now,
                };
                db.IncidentObservations.Add(moved);
                split.Observations.Add(moved);
            }
            source.SourceCount = source.Observations.Select(o => o.SourceId).Distinct().Count();
            source.FirstReportedAt = source.Observations.Min(o => o.EffectiveAt);
            source.LastReportedAt = source.Observations.Max(o => o.EffectiveAt);
            source.EventAt = source.FirstReportedAt;
            if (!source.Observations.Any(o => o.ObservationId == source.CanonicalObservationId))
            {
                source.CanonicalObservationId = source.Observations.OrderBy(o => o.EffectiveAt).First().ObservationId;
            }
            source.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return [(split, "split", observationIds, "split from " + id), (source, "updated", observationIds, "split into new incident")];
        }, actor, reason, null, ct);
    }

    private async Task<IncidentChange> TransitionAsync(long id, string actor, string reason, DateTimeOffset? effectiveAt, string change, string state, string? closure, string[] from, CancellationToken ct)
    {
        RequireActor(actor, reason);
        var changes = await CommandAsync([id], async (db, incidents) =>
        {
            var incident = incidents[0];
            if (!from.Contains(incident.State, StringComparer.Ordinal))
            {
                throw new IncidentConflictException($"incident {id} is {incident.State}; {change} needs one of {string.Join("/", from)}");
            }
            incident.State = state;
            incident.ClosureReason = closure;
            incident.UpdatedAt = effectiveAt ?? clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return [(incident, change, (IReadOnlyList<Guid>)[], (string?)null)];
        }, actor, reason, effectiveAt, ct);
        return changes[0];
    }

    private async Task<IReadOnlyList<IncidentChange>> CommandAsync(long[] ids, Func<PulujDbContext, List<Incident>, Task<List<(Incident Incident, string Change, IReadOnlyList<Guid> Observations, string? Note)>>> apply,
        string actor, string reason, DateTimeOffset? effectiveAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await db.Database.UseTransactionAsync(tx, ct);
        await WriterSupport.LockStoreSharedAsync(conn, tx, ct);
        var kindIds = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.IncidentId)).Select(i => i.EventKindId).Distinct().ToListAsync(ct);
        foreach (var kindId in kindIds.Order())
        {
            await WriterSupport.LockAsync(conn, tx, $"incident:kind:{kindId}", shared: false, ct);
        }
        var incidents = await db.Incidents.Include(i => i.Observations).Where(i => ids.Contains(i.IncidentId)).ToListAsync(ct);
        foreach (var id in ids)
        {
            if (incidents.All(i => i.IncidentId != id))
            {
                throw new IncidentNotFoundException(id);
            }
        }
        var results = await apply(db, incidents);
        var runId = await outbox.Runs.GetOpenRunAsync(conn, tx, "live", ct);
        var changes = new List<IncidentChange>();
        var now = clock.GetUtcNow();
        foreach (var (incident, change, observations, note) in results)
        {
            var cause = CommandCause(incident, runId, now);
            var revised = await ReviseAsync(db, incident, change, observations, cause, actor, note is null ? reason : $"{reason} ({note})", effectiveAt ?? incident.UpdatedAt, now, ct);
            await outbox.EnqueueAsync(conn, tx, revised.Event, ct);
            changes.Add(revised);
        }
        await tx.CommitAsync(ct);
        foreach (var c in changes)
        {
            logger.LogInformation("Incident {Id} {Change} by {Actor} → revision {Revision} ({Reason})", c.Incident.IncidentId, c.Change, actor, c.Incident.Revision, reason);
        }
        return changes;
    }

    /// <summary>revision + 1, the revision row (as-of snapshot) and the `incident.changed` envelope; the caller writes the event to the outbox.</summary>
    public async Task<IncidentChange> ReviseAsync(PulujDbContext db, Incident incident, string change, IReadOnlyList<Guid> observationIds, Envelope cause, string actor, string? reason, DateTimeOffset effectiveAt, DateTimeOffset recordedAt, CancellationToken ct)
    {
        var eventId = Guid.CreateVersion7();
        incident.Revision++;
        incident.LastEventId = eventId;
        incident.LastCorrelationId = cause.CorrelationId;
        db.IncidentRevisions.Add(new IncidentRevision
        {
            Incident = incident,
            Revision = incident.Revision,
            Change = change,
            EffectiveAt = effectiveAt,
            RecordedAt = recordedAt,
            TriggeringEventId = cause.EventId,
            Actor = actor,
            Reason = reason,
            Snapshot = JsonDocument.Parse(Snapshot(incident).ToJsonString()),
        });
        await db.SaveChangesAsync(ct);
        var kindCode = KindCode(incident.EventKindId);
        var payload = new JsonObject
        {
            ["incident_id"] = incident.IncidentId,
            ["change"] = change,
            ["revision"] = incident.Revision,
            ["effective_at"] = FactMapper.Iso(effectiveAt),
            ["recorded_at"] = FactMapper.Iso(recordedAt),
            ["observation_ids"] = WriterSupport.Ids(observationIds),
            ["event_kind_code"] = kindCode,
            ["state"] = incident.State,
            ["generation_id"] = incident.GenerationId.ToString(),
            ["suppressed"] = incident.Suppressed,
            ["policy_version"] = IncidentPolicy.Version,
            ["location"] = Location(incident),
        };
        if (reason is not null)
        {
            payload["reason"] = reason;
        }
        if (incident.MergedIntoIncidentId is long merged)
        {
            payload["merged_into_incident_id"] = merged;
        }
        var envelope = WriterSupport.AggregateEvent(cause, EventType, Instance, effectiveAt, $"incident:{incident.IncidentId}", incident.Revision, $"incident:kind:{kindCode}", payload, eventId);
        return new IncidentChange(incident, change, observationIds, envelope);
    }

    /// <summary>The as-of snapshot: everything the read side needs to show the incident as it was at this revision.</summary>
    public JsonObject Snapshot(Incident i) => new()
    {
        ["incident_id"] = i.IncidentId,
        ["generation_id"] = i.GenerationId.ToString(),
        ["event_kind_code"] = KindCode(i.EventKindId),
        ["state"] = i.State,
        ["suppressed"] = i.Suppressed,
        ["first_reported_at"] = FactMapper.Iso(i.FirstReportedAt),
        ["last_reported_at"] = FactMapper.Iso(i.LastReportedAt),
        ["event_at"] = FactMapper.Iso(i.EventAt),
        ["location"] = Location(i),
        ["confidence"] = FactMapper.Confidence(i.Confidence),
        ["source_count"] = i.SourceCount,
        ["independent_source_count"] = i.IndependentSourceCount,
        ["canonical_observation_id"] = i.CanonicalObservationId?.ToString(),
        ["closure_reason"] = i.ClosureReason,
        ["merged_into_incident_id"] = i.MergedIntoIncidentId,
        ["revision"] = i.Revision,
        ["observations"] = new JsonArray(i.Observations.OrderBy(o => o.EffectiveAt).ThenBy(o => o.ObservationId).Select(o => (JsonNode)new JsonObject
        {
            ["observation_id"] = o.ObservationId.ToString(),
            ["relation"] = o.Relation,
            ["source_id"] = o.SourceId,
            ["score"] = Math.Round(o.Score, 3),
            ["effective_at"] = FactMapper.Iso(o.EffectiveAt),
        }).ToArray()),
    };

    /// <summary>§8.5: the place and its accuracy; a point only when the evidence carries one (a coarse report is an area with a visible error radius, never a pin).</summary>
    private static JsonObject Location(Incident i)
    {
        var location = new JsonObject { ["kind"] = FactMapper.LocationKind(i.LocationKind) };
        if (i.LocationPlaceId is int place)
        {
            location["place_id"] = place;
        }
        if (i.AccuracyKm is double km)
        {
            location["accuracy_km"] = Math.Round(km, 3);
        }
        if (i.Geometry is { } g)
        {
            location["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(Math.Round(g.Centroid.Coordinate.X, 6), Math.Round(g.Centroid.Coordinate.Y, 6)) };
        }
        return location;
    }

    private Envelope CommandCause(Incident incident, Guid runId, DateTimeOffset now) => new()
    {
        EventId = incident.LastEventId ?? SourceIdentity.NameBasedGuid(Namespace, $"legacy:incident:{incident.IncidentId}"),
        EventType = EventType,
        SchemaVersion = "1.0",
        Producer = Instance,
        OccurredAt = now,
        CorrelationId = incident.LastCorrelationId ?? SourceIdentity.NameBasedGuid(Namespace, $"incident:{incident.IncidentId}"),
        CausationId = null,
        Traceparent = RawStoredEnvelope.CurrentTraceparent(),
        ProcessingRunId = runId,
        PipelineVersion = outbox.Runs.PipelineVersion,
        Lane = "live",
    };

    private string KindCode(int kindId) => indexes.EventKinds.CodeOf(kindId) ?? kindId.ToString();

    private int? RegionOf(int? placeId) => placeId is int id && indexes.Gazetteer.Get(id) is { } p ? indexes.Gazetteer.RegionOf(p)?.PlaceId : null;

    private static SpatialAnchor? AnchorOf(Incident i, GazetteerIndex gazetteer) =>
        i.Geometry is null ? null : new SpatialAnchor(i.Geometry.Centroid.Coordinate, i.AccuracyKm ?? 0, i.LocationPlaceId, gazetteer.Get(i.LocationPlaceId ?? -1)?.Boundary);

    private static void RequireActor(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("actor and reason are required");
        }
    }
}
