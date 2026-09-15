using System.Text.Json;
using System.Text.Json.Nodes;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;

namespace Puluj.Processing.Stages;

/// <summary>
/// Maps an in-memory <see cref="Target"/> (built by the rules or the structured adapter, never saved here) to the
/// contract fact of `parse.completed.facts[]` (`schemas/events/parse.completed.schema.json`): catalog kind + category,
/// effective time, location, confidence, evidence, and — under `attributes` — every field of the legacy target, so the
/// fact writer (P06) can produce exactly what the legacy pipeline produced and a drift between the two is visible
/// field by field (P05-S08). The mapping is deterministic: the same target always gives the same JSON.
/// </summary>
public static class FactMapper
{
    public static JsonObject ToFact(Target t, EventKindIndex kinds, GazetteerIndex gazetteer, ParsedFact? fact, string? language, string rulesVersion)
    {
        var code = EventKindLegacyMap.ToCode(t.EventType);
        var kind = kinds.ByCode(code);
        var category = kind?.Category ?? CategoryFallback(t.EventType);
        var json = new JsonObject
        {
            ["event_kind_code"] = code,
            ["category"] = category.ToString().ToLowerInvariant(),
            ["effective_at"] = Iso(t.ObservedAt),
            ["location"] = Location(t, gazetteer),
            ["confidence"] = Confidence(t.Confidence),
            ["evidence"] = Evidence(t, fact, rulesVersion),
            ["attributes"] = Attributes(t, fact, language, kind),
        };
        return json;
    }

    private static JsonObject Location(Target t, GazetteerIndex gazetteer)
    {
        var location = new JsonObject { ["kind"] = LocationKind(t.LocationKind) };
        if (t.LocationPlaceId is int placeId)
        {
            location["place_id"] = placeId;
        }
        if (t.Location is { } geometry)
        {
            location["geometry"] = new JsonObject
            {
                ["type"] = "Point",
                ["coordinates"] = new JsonArray(Math.Round(geometry.Coordinate.X, 6), Math.Round(geometry.Coordinate.Y, 6)),
            };
        }
        if (t.LocationAccuracyKm is double km)
        {
            location["accuracy_km"] = Math.Round(km, 3);
        }
        var name = t.LocationPlace?.Name ?? (t.LocationPlaceId is int id ? gazetteer.Get(id)?.Name : null);
        if (name is null && t.ParserMetadata is { } meta && meta.RootElement.TryGetProperty("title", out var title) && title.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            name = title.GetString(); // structured feed named a place the gazetteer does not know: keep the text, not a guess
        }
        if (!string.IsNullOrEmpty(name))
        {
            location["text"] = name;
        }
        return location;
    }

    private static JsonObject Evidence(Target t, ParsedFact? fact, string rulesVersion)
    {
        var evidence = new JsonObject { ["segment_index"] = t.SegmentIndex };
        if (t.SegmentText is { Length: > 0 } text)
        {
            evidence["span"] = new JsonObject { ["start"] = 0, ["end"] = text.Length };
        }
        if (t.IdentificationMethod == IdentificationMethod.Structured)
        {
            evidence["structured_field"] = "alert";
            evidence["rule_version"] = t.ParserVersion;
        }
        else
        {
            evidence["rule_id"] = fact is { Rules.Count: > 0 } ? string.Join("+", fact.Rules) : "rules";
            evidence["rule_version"] = rulesVersion;
            if (fact is { Rules.Count: > 0 })
            {
                evidence["rules"] = new JsonArray(fact.Rules.Select(r => (JsonNode)r).ToArray());
            }
        }
        return evidence;
    }

    /// <summary>Every legacy field, so nothing the old pipeline wrote is lost on the way to the fact writer.</summary>
    private static JsonObject Attributes(Target t, ParsedFact? fact, string? language, EventKind? kind)
    {
        var a = new JsonObject
        {
            ["legacy_event_type"] = t.EventType.ToString(),
            ["identification_method"] = t.IdentificationMethod.ToString(),
            ["identification_source"] = t.IdentificationSource,
            ["parser_version"] = t.ParserVersion,
            ["segment_index"] = t.SegmentIndex,
            ["segment_text"] = t.SegmentText,
            ["language"] = language,
            ["alert_level"] = t.AlertLevel.ToString(),
            ["target_category_id"] = t.TargetCategoryId,
            ["target_class_id"] = t.TargetClassId,
            ["target_family_id"] = t.TargetFamilyId,
            ["target_model_id"] = t.TargetModelId,
            ["model_confidence"] = Confidence(t.ModelConfidence),
            ["classification_confidence"] = Confidence(t.ClassificationConfidence),
            ["object_count"] = t.ObjectCount,
            ["object_count_is_approximate"] = t.ObjectCountIsApproximate,
            ["location_kind"] = LocationKind(t.LocationKind),
            ["location_place_id"] = t.LocationPlaceId,
            ["location_accuracy_km"] = t.LocationAccuracyKm is double km ? Math.Round(km, 3) : null,
            ["origin_place_id"] = t.OriginPlaceId,
            ["destination_place_id"] = t.DestinationPlaceId,
            ["direction_kind"] = t.DirectionKind.ToString(),
            ["direction_deg"] = t.DirectionDeg is double deg ? Math.Round(deg, 2) : null,
            ["direction_confidence"] = Confidence(t.DirectionConfidence),
            ["confidence"] = Confidence(t.Confidence),
            ["event_kind_id"] = kind?.EventKindId,
            ["parser_metadata"] = t.ParserMetadata is { } meta ? JsonNode.Parse(meta.RootElement.GetRawText()) : null,
        };
        if (fact is not null)
        {
            // Rule-only knowledge that the saved row does not keep.
            a["hedged"] = fact.Target?.Hedged ?? false;
            a["is_launch"] = fact.IsLaunch;
        }
        if (fact is not null && fact.Places.Count > 0)
        {
            a["places"] = new JsonArray(fact.Places.Select(p => (JsonNode)new JsonObject
            {
                ["place_id"] = p.Place.PlaceId,
                ["role"] = p.Role.ToString().ToLowerInvariant(),
                ["matched_text"] = p.MatchedText,
                ["score"] = p.Score,
                ["quadrant_deg"] = p.QuadrantDeg,
            }).ToArray());
        }
        return a;
    }

    public static string Confidence(ConfidenceLevel c) => c switch
    {
        ConfidenceLevel.Low => "low",
        ConfidenceLevel.Medium => "medium",
        ConfidenceLevel.High => "high",
        ConfidenceLevel.Confirmed => "confirmed",
        _ => "unknown",
    };

    public static string LocationKind(LocationKind k) => k switch
    {
        Domain.Enums.LocationKind.DirectionOnly => "direction_only",
        Domain.Enums.LocationKind.Region => "region",
        Domain.Enums.LocationKind.District => "district",
        Domain.Enums.LocationKind.City => "city",
        Domain.Enums.LocationKind.Area => "area",
        Domain.Enums.LocationKind.Point => "point",
        _ => "unknown",
    };

    private static EventKindCategory CategoryFallback(EventType type) => type switch
    {
        EventType.AirRaidAlert or EventType.AlertCancelled => EventKindCategory.Alert,
        EventType.ExplosionReport or EventType.AirDefenseActivity => EventKindCategory.Incident,
        EventType.Unknown => EventKindCategory.Info,
        _ => EventKindCategory.Target,
    };

    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O");

    /// <summary>Stable text of a node with object keys sorted (jsonb reorders keys; tests compare legacy targets with stage facts through the same mapper).</summary>
    public static string Canonical(JsonNode? node) => JsonSerializer.Serialize(Sorted(node), new JsonSerializerOptions { WriteIndented = false });

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        _ => node?.DeepClone(),
    };
}
