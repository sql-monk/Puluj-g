using System.Text.Json;
using NetTopologySuite.Geometries;
using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>Spec §6. A single fact extracted from one RawMessage.</summary>
public class Target
{
    public long TargetId { get; set; }
    public long RawMessageId { get; set; }
    public RawMessage? RawMessage { get; set; }
    public int SourceId { get; set; }
    public Source? Source { get; set; }
    /// <summary>Index of the fact inside the message (a message may yield several facts).</summary>
    public int SegmentIndex { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
    public EventType EventType { get; set; }
    /// <summary>
    /// Plan §8.2. Catalog kind of this fact (event_kinds.code). Written together with the legacy <see cref="EventType"/>
    /// during the compatibility window; null for rows the backfill has not reached yet or when the catalog was empty.
    /// </summary>
    public int? EventKindId { get; set; }
    public EventKind? EventKind { get; set; }
    /// <summary>For AirRaidAlert events: the level the source stated ("жовтий рівень"), if any.</summary>
    public AirAlertLevel AlertLevel { get; set; }

    // Classification (spec §7–§9)
    public int? TargetCategoryId { get; set; }
    public int? TargetClassId { get; set; }
    public int? TargetFamilyId { get; set; }
    public int? TargetModelId { get; set; }
    public ConfidenceLevel ModelConfidence { get; set; }
    public ConfidenceLevel ClassificationConfidence { get; set; }
    public IdentificationMethod IdentificationMethod { get; set; }
    /// <summary>What the identification is based on: alias text, LLM model id, source name.</summary>
    public string? IdentificationSource { get; set; }

    public int? ObjectCount { get; set; }
    public bool ObjectCountIsApproximate { get; set; }

    // Location
    public LocationKind LocationKind { get; set; }
    public int? LocationPlaceId { get; set; }
    public Place? LocationPlace { get; set; }
    /// <summary>Geometry as reported (polygon for regions, point only when the source gave one). SRID 4326.</summary>
    public Geometry? Location { get; set; }
    public double? LocationAccuracyKm { get; set; }
    public int? OriginPlaceId { get; set; }
    public int? DestinationPlaceId { get; set; }

    // Direction
    public DirectionKind DirectionKind { get; set; }
    public double? DirectionDeg { get; set; }
    public ConfidenceLevel DirectionConfidence { get; set; }

    public ConfidenceLevel Confidence { get; set; }

    /// <summary>Set when this target repeats an earlier one (kept for provenance).</summary>
    public long? DuplicateOfTargetId { get; set; }
    public string ParserVersion { get; set; } = "";
    /// <summary>Matched rules, text spans, LLM prompt version — everything needed to explain the result.</summary>
    public JsonDocument? ParserMetadata { get; set; }
    /// <summary>The text segment the fact was extracted from.</summary>
    public string? SegmentText { get; set; }
}
