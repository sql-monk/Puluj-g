using System.Text.Json;
using NetTopologySuite.Geometries;
using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>
/// Plan §8.4 (P10). One static event (an explosion, a fire, an outage…) as the incident-worker sees it: the aggregate over
/// the observations linked to it. Evidence is never edited here — every change is a new revision, the rows in
/// <see cref="IncidentObservation"/> say why an observation belongs (score, decision) and the state moves only by policy or
/// by an admin command through the same writer.
/// </summary>
public class Incident
{
    public const string Reported = "reported";
    public const string Confirmed = "confirmed";
    public const string Resolved = "resolved";
    public const string Retracted = "retracted";

    public long IncidentId { get; set; }
    /// <summary>Result set the incident belongs to (ADR-0005); replay with another generation never joins these rows.</summary>
    public Guid GenerationId { get; set; }
    public Guid? RunId { get; set; }
    public int EventKindId { get; set; }
    public EventKind? EventKind { get; set; }
    public string State { get; set; } = Reported;
    /// <summary>Hidden by an operator without changing the state (a hoax, a duplicate kept for its evidence).</summary>
    public bool Suppressed { get; set; }
    public DateTimeOffset FirstReportedAt { get; set; }
    public DateTimeOffset LastReportedAt { get; set; }
    /// <summary>When the event happened according to the evidence (the earliest effective time); the candidate window is measured from here.</summary>
    public DateTimeOffset EventAt { get; set; }
    public LocationKind LocationKind { get; set; }
    public int? LocationPlaceId { get; set; }
    /// <summary>The most precise evidence location; never a made-up point (§8.5): null without located evidence.</summary>
    public Geometry? Geometry { get; set; }
    public double? AccuracyKm { get; set; }
    public ConfidenceLevel Confidence { get; set; }
    public int SourceCount { get; set; }
    /// <summary>No method to assess independence exists yet: always null (never the channel count).</summary>
    public int? IndependentSourceCount { get; set; }
    public Guid? CanonicalObservationId { get; set; }
    public int Revision { get; set; }
    public string? ClosureReason { get; set; }
    public long? MergedIntoIncidentId { get; set; }
    public Guid? LastEventId { get; set; }
    public Guid? LastCorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<IncidentObservation> Observations { get; set; } = [];
}

/// <summary>Evidence link: why an observation is part of an incident. One observation belongs to at most one incident.</summary>
public class IncidentObservation
{
    public const string Canonical = "canonical";
    public const string Supports = "supports";
    public const string Echo = "echo";
    public const string Confirms = "confirms";
    public const string Ambiguous = "ambiguous";
    public const string Moved = "moved";

    public long IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public Guid ObservationId { get; set; }
    public Guid GenerationId { get; set; }
    public long? LegacyTargetId { get; set; }
    public int SourceId { get; set; }
    public required string Relation { get; set; }
    public double Score { get; set; }
    public JsonDocument? DecisionReason { get; set; }
    public required string PolicyVersion { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>Append-only snapshot after every change of an incident: the as-of history (§8.5) and the audit of admin commands.</summary>
public class IncidentRevision
{
    public long IncidentRevisionId { get; set; }
    public long IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public int Revision { get; set; }
    public required string Change { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? TriggeringEventId { get; set; }
    public required string Actor { get; set; }
    public string? Reason { get; set; }
    public required JsonDocument Snapshot { get; set; }
}
