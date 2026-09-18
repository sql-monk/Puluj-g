using System.Text.Json;
using NetTopologySuite.Geometries;
using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>Spec §10. Logical object formed from one or more targets.</summary>
public class TargetTrack
{
    public long TargetTrackId { get; set; }
    public TrackStatus Status { get; set; } = TrackStatus.Active;
    public string? ClosedReason { get; set; }

    public int TargetCategoryId { get; set; }
    public int? TargetClassId { get; set; }
    public int? TargetFamilyId { get; set; }
    public int? TargetModelId { get; set; }
    public ConfidenceLevel ModelConfidence { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    /// <summary>Event time of the last state change (target time, or closure time). Revisions are stamped with it.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public LocationKind LastLocationKind { get; set; }
    public int? LastLocationPlaceId { get; set; }
    public Geometry? LastLocation { get; set; }
    public double? LastLocationAccuracyKm { get; set; }
    /// <summary>Centroids of successive targets, oldest first. Null until 2+ located targets.</summary>
    public LineString? TrackGeometry { get; set; }

    public DirectionKind DirectionKind { get; set; }
    public double? DirectionDeg { get; set; }
    public ConfidenceLevel DirectionConfidence { get; set; }
    public int? ObjectCount { get; set; }

    public ConfidenceLevel TrackConfidence { get; set; }
    public int TargetCount { get; set; }
    public int DistinctSourceCount { get; set; }
    /// <summary>Source and id of the newest target, used by the correlator to keep one source's parallel reports apart.</summary>
    public int? LastSourceId { get; set; }
    public long? LastTargetId { get; set; }
    public ICollection<TrackTarget> Targets { get; set; } = [];
}

/// <summary>Spec §10 link table.</summary>
public class TrackTarget
{
    public long TargetTrackId { get; set; }
    public TargetTrack? Track { get; set; }
    public long TargetId { get; set; }
    public Target? Target { get; set; }
    public int Sequence { get; set; }
    /// <summary>0..1 score produced by the correlator.</summary>
    public double AssociationConfidence { get; set; }
    /// <summary>Breakdown of the score (time/space/direction/class components).</summary>
    public JsonDocument? AssociationReason { get; set; }
}

/// <summary>Append-only snapshot of a track after every change: the basis for historical replay (spec §20).</summary>
public class TargetTrackRevision
{
    public long TargetTrackRevisionId { get; set; }
    public long TargetTrackId { get; set; }
    public TargetTrack? Track { get; set; }
    public DateTimeOffset RevisionAt { get; set; }
    public TrackStatus Status { get; set; }
    public int TargetCategoryId { get; set; }
    public int? TargetClassId { get; set; }
    public int? TargetFamilyId { get; set; }
    public int? TargetModelId { get; set; }
    public ConfidenceLevel ModelConfidence { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public LocationKind LastLocationKind { get; set; }
    public int? LastLocationPlaceId { get; set; }
    public Geometry? LastLocation { get; set; }
    public double? LastLocationAccuracyKm { get; set; }
    public LineString? TrackGeometry { get; set; }
    public DirectionKind DirectionKind { get; set; }
    public double? DirectionDeg { get; set; }
    public ConfidenceLevel DirectionConfidence { get; set; }
    public int? ObjectCount { get; set; }
    public ConfidenceLevel TrackConfidence { get; set; }
    public int TargetCount { get; set; }
    /// <summary>Target that triggered this revision, if any.</summary>
    public long? TargetId { get; set; }
}
