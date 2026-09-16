using System.Text.Json;
using System.Text.Json.Nodes;
using NetTopologySuite.Geometries;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Stages;

namespace Puluj.Processing.Writers;

/// <summary>
/// P09: the inverse of <see cref="FactMapper"/> for the compatibility projection — a contract fact of
/// `observations.recorded` back into the legacy <see cref="Target"/> row the domain writers insert. Every column the
/// legacy pipeline wrote travels in `attributes` (P05-S08 parity), so the row is identical up to the id and the new
/// `observation_id`. Rule-only knowledge (`hedged`, `is_launch`, `places`) has no column and is not restored.
/// </summary>
public static class TargetMaterializer
{
    private static readonly GeometryFactory Geometry = new(new PrecisionModel(), 4326);

    public static Target FromFact(JsonObject fact, long rawMessageId, int sourceId)
    {
        var a = fact["attributes"]?.AsObject() ?? throw new ArgumentException("fact without attributes", nameof(fact));
        var location = fact["location"]?.AsObject();
        var t = new Target
        {
            RawMessageId = rawMessageId,
            SourceId = sourceId,
            ObservationId = Guid.TryParse(fact["observation_id"]?.GetValue<string>(), out var oid) ? oid : null,
            SegmentIndex = a["segment_index"]?.GetValue<int>() ?? fact["evidence"]?["segment_index"]?.GetValue<int>() ?? 0,
            SegmentText = a["segment_text"]?.GetValue<string>(),
            ObservedAt = DateTimeOffset.Parse(fact["effective_at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            EventType = Enum.TryParse<EventType>(a["legacy_event_type"]?.GetValue<string>(), out var et) ? et : EventType.Unknown,
            EventKindId = a["event_kind_id"]?.GetValue<int>(),
            AlertLevel = Enum.TryParse<AirAlertLevel>(a["alert_level"]?.GetValue<string>(), out var al) ? al : AirAlertLevel.Unknown,
            TargetCategoryId = a["target_category_id"]?.GetValue<int>(),
            TargetClassId = a["target_class_id"]?.GetValue<int>(),
            TargetFamilyId = a["target_family_id"]?.GetValue<int>(),
            TargetModelId = a["target_model_id"]?.GetValue<int>(),
            ModelConfidence = Confidence(a["model_confidence"]),
            ClassificationConfidence = Confidence(a["classification_confidence"]),
            IdentificationMethod = Enum.TryParse<IdentificationMethod>(a["identification_method"]?.GetValue<string>(), out var im) ? im : IdentificationMethod.Rule,
            IdentificationSource = a["identification_source"]?.GetValue<string>(),
            ParserVersion = a["parser_version"]?.GetValue<string>() ?? "",
            ObjectCount = a["object_count"]?.GetValue<int>(),
            ObjectCountIsApproximate = a["object_count_is_approximate"]?.GetValue<bool>() ?? false,
            LocationKind = LocationKind(a["location_kind"]?.GetValue<string>() ?? location?["kind"]?.GetValue<string>()),
            LocationPlaceId = a["location_place_id"]?.GetValue<int>() ?? location?["place_id"]?.GetValue<int>(),
            LocationAccuracyKm = a["location_accuracy_km"]?.GetValue<double>(),
            OriginPlaceId = a["origin_place_id"]?.GetValue<int>(),
            DestinationPlaceId = a["destination_place_id"]?.GetValue<int>(),
            DirectionKind = Enum.TryParse<DirectionKind>(a["direction_kind"]?.GetValue<string>(), out var dk) ? dk : DirectionKind.Unknown,
            DirectionDeg = a["direction_deg"]?.GetValue<double>(),
            DirectionConfidence = Confidence(a["direction_confidence"]),
            Confidence = Confidence(a["confidence"] ?? fact["confidence"]),
            ParserMetadata = a["parser_metadata"] is { } meta ? JsonDocument.Parse(meta.ToJsonString()) : null,
        };
        if (location?["geometry"] is JsonObject geometry && geometry["type"]?.GetValue<string>() == "Point" && geometry["coordinates"] is JsonArray c && c.Count >= 2)
        {
            t.Location = Geometry.CreatePoint(new Coordinate(c[0]!.GetValue<double>(), c[1]!.GetValue<double>()));
        }
        return t;
    }

    public static ConfidenceLevel Confidence(JsonNode? node) => node?.GetValue<string>() switch
    {
        "low" => ConfidenceLevel.Low,
        "medium" => ConfidenceLevel.Medium,
        "high" => ConfidenceLevel.High,
        "confirmed" => ConfidenceLevel.Confirmed,
        _ => ConfidenceLevel.Unknown,
    };

    public static LocationKind LocationKind(string? kind) => kind switch
    {
        "direction_only" => Domain.Enums.LocationKind.DirectionOnly,
        "region" => Domain.Enums.LocationKind.Region,
        "district" => Domain.Enums.LocationKind.District,
        "city" => Domain.Enums.LocationKind.City,
        "area" => Domain.Enums.LocationKind.Area,
        "point" => Domain.Enums.LocationKind.Point,
        _ => Domain.Enums.LocationKind.Unknown,
    };

    /// <summary>The domain branch a fact belongs to (`category` of the contract fact): `target`, `alert`, `incident`, `info`.</summary>
    public static string Category(JsonObject fact) => fact["category"]?.GetValue<string>() ?? "info";
}
