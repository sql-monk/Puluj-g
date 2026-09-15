using System.Text.Json;
using System.Text.Json.Nodes;
using NetTopologySuite.Geometries;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;

namespace Puluj.Processing.Pipeline;

/// <summary>
/// Geocoder + Classifier (spec §4): turns a ParsedFact into an Target with honest location kinds and
/// confidences derived from the alias precision and the source trust level. Never invents precision.
/// </summary>
public sealed class TargetBuilder(IIndexes indexes)
{
    public Target Build(ParsedFact fact, RawMessage raw, Source source, string parserVersion, IdentificationMethod method, string language)
    {
        var gazetteer = indexes.Gazetteer;
        var obs = new Target
        {
            RawMessageId = raw.RawMessageId,
            SourceId = source.SourceId,
            SegmentIndex = fact.SegmentIndex,
            SegmentText = fact.SegmentText,
            ObservedAt = raw.PublishedAt,
            EventType = fact.EventType,
            AlertLevel = fact.AlertLevel,
            IdentificationMethod = method,
            ParserVersion = parserVersion,
            ObjectCount = fact.Count,
            ObjectCountIsApproximate = fact.CountIsApproximate,
            // The catalog kind named by the rule (P08) — also for kinds without a legacy enum member; the legacy stamp fills the rest.
            EventKindId = fact.EventKindCode is { } kindCode ? indexes.EventKinds.ByCode(kindCode)?.EventKindId : null,
        };

        var trustCap = source.TrustLevel switch { >= 0.85 => ConfidenceLevel.High, >= 0.6 => ConfidenceLevel.Medium, _ => ConfidenceLevel.Low };

        if (fact.Target is { } target)
        {
            obs.TargetCategoryId = target.Ref.CategoryId;
            obs.TargetClassId = target.Ref.ClassId;
            obs.TargetFamilyId = target.Ref.FamilyId;
            obs.TargetModelId = target.Ref.ModelId;
            obs.IdentificationSource = method == IdentificationMethod.Llm ? $"{parserVersion}: {target.MatchedText}" : target.MatchedText;
            // Confidence in the deepest identified level, never above what the source itself asserts.
            obs.ModelConfidence = target.Ref.Level is AliasTargetLevel.Model or AliasTargetLevel.Family
                ? Min(target.EffectiveConfidence, trustCap)
                : ConfidenceLevel.Unknown;
            obs.ClassificationConfidence = Min(target.EffectiveConfidence, trustCap);
        }

        obs.Confidence = method == IdentificationMethod.Structured ? ConfidenceLevel.Confirmed : trustCap;
        if (fact.Target?.Hedged == true)
        {
            obs.Confidence = Lower(obs.Confidence);
        }

        // Location: the current area if reported, otherwise the origin ("з Чернігівщини" = it is leaving that region).
        var located = fact.Current ?? fact.Origin;
        if (located is not null)
        {
            obs.LocationPlaceId = located.Place.PlaceId;
            obs.LocationKind = KindFor(located.Place.Level);
            if (located.QuadrantDeg is double q && located.Place.RadiusKm > 20)
            {
                // "на півночі Київщини": still the region, but the northern half of it.
                obs.Location = Geo.Offset(located.Place.Centroid.Coordinate, q, located.Place.RadiusKm * 0.5);
                obs.LocationAccuracyKm = located.Place.RadiusKm * 0.6;
            }
            else
            {
                obs.Location = located.Place.Centroid;
                obs.LocationAccuracyKm = located.Place.RadiusKm;
            }
            // "Фастівський район": the gazetteer has no raions, so the raion resolves to its town — widen it honestly.
            if (obs.LocationKind == LocationKind.City && NamesRaion(fact.SegmentText, located.MatchedText))
            {
                obs.LocationKind = LocationKind.District;
                obs.LocationAccuracyKm = Math.Max(obs.LocationAccuracyKm ?? 0, RaionKm);
            }
        }
        else
        {
            obs.LocationKind = fact.Direction is not null || fact.Destination is not null ? LocationKind.DirectionOnly : LocationKind.Unknown;
        }
        obs.OriginPlaceId = fact.Origin?.Place.PlaceId;
        obs.DestinationPlaceId = fact.Destination?.Place.PlaceId;

        // Direction: reported compass words first; otherwise bearing towards the named destination.
        if (fact.Direction is { } dir)
        {
            obs.DirectionDeg = dir.Degrees;
            obs.DirectionKind = dir.Kind;
            obs.DirectionConfidence = Min(ConfidenceLevel.High, trustCap);
        }
        else if (fact.Destination is { } dest && located is not null && dest.Place.PlaceId != located.Place.PlaceId
                 && DestinationInside(dest.Place, located.Place) && gazetteer.ApproachBearingTo(dest.Place.Centroid.Coordinate) is double approach)
        {
            // "Київ: БпЛА курсом на Троєщину" — the district is inside the city, so a bearing from the city centre says
            // nothing. The object is on the approach to the district, coming in from the hostile side.
            var anchor = ApproachAnchor(dest.Place, approach);
            obs.Location = anchor.Point;
            obs.LocationKind = LocationKind.DirectionOnly;
            obs.LocationPlaceId = dest.Place.PlaceId;
            obs.LocationAccuracyKm = anchor.AccuracyKm;
            obs.DirectionDeg = approach;
            obs.DirectionKind = DirectionKind.TowardsPlace;
            obs.DirectionConfidence = Min(ConfidenceLevel.Low, trustCap);
        }
        else if (fact.Destination is { } dest2 && located is not null && dest2.Place.PlaceId != located.Place.PlaceId)
        {
            // Bearing from where the marker is drawn towards the named destination, so the vector on the map always
            // points at the place the text names. From the centre of a coarse area it is only a hint: Low confidence.
            obs.DirectionDeg = Geo.BearingDeg(located.Place.Centroid.Coordinate, dest2.Place.Centroid.Coordinate);
            obs.DirectionKind = DirectionKind.TowardsPlace;
            obs.DirectionConfidence = Min(located.Place.RadiusKm <= PreciseKm ? ConfidenceLevel.Medium : ConfidenceLevel.Low, trustCap);
        }
        else
        {
            obs.DirectionKind = DirectionKind.Unknown;
            obs.DirectionConfidence = ConfidenceLevel.Unknown;
        }

        obs.ParserMetadata = Metadata(fact, language, gazetteer);
        return obs;
    }

    /// <summary>How far short of the destination the approach anchor sits, km: close enough to read as "almost there".</summary>
    public const double ApproachKm = 15;

    /// <summary>The destination lies within the area the message locates the object in (a district of the city, a town of the oblast).</summary>
    public static bool DestinationInside(PlaceEntry destination, PlaceEntry located) =>
        destination.ParentId == located.PlaceId
        || (located.RadiusKm > PreciseKm && Geo.DistanceKm(located.Centroid.Coordinate, destination.Centroid.Coordinate) <= located.RadiusKm);

    /// <summary>A point ApproachKm short of the destination along the approach course, with the accuracy that claim deserves.</summary>
    public static (Point Point, double AccuracyKm) ApproachAnchor(PlaceEntry destination, double approachBearing) =>
        (Geo.Offset(destination.Centroid.Coordinate, (approachBearing + 180) % 360, ApproachKm), Math.Max(ApproachKm, destination.RadiusKm));

    /// <summary>Typical half-extent of a raion, used when a raion is named but only its town is in the gazetteer.</summary>
    private const double RaionKm = 25;

    private static bool NamesRaion(string segment, string matched)
    {
        var i = segment.IndexOf(matched, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return false;
        }
        var rest = segment[(i + matched.Length)..].TrimStart();
        return rest.StartsWith("район", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("р-н", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A place this small is a usable point for bearings; larger ones are areas whose centre says little.</summary>
    private const double PreciseKm = 25;

    public static LocationKind KindFor(PlaceLevel level) => level switch
    {
        PlaceLevel.Country or PlaceLevel.Region => LocationKind.Region,
        PlaceLevel.District or PlaceLevel.Hromada => LocationKind.District,
        PlaceLevel.NamedArea => LocationKind.Area,
        _ => LocationKind.City,
    };

    private static ConfidenceLevel Min(ConfidenceLevel a, ConfidenceLevel b) => (ConfidenceLevel)Math.Min((int)a, (int)b);

    private static ConfidenceLevel Lower(ConfidenceLevel c) => c == ConfidenceLevel.Unknown ? c : (ConfidenceLevel)Math.Max((int)ConfidenceLevel.Low, (int)c - 1);

    private JsonDocument Metadata(ParsedFact fact, string language, GazetteerIndex gazetteer)
    {
        var o = new JsonObject
        {
            ["language"] = language,
            ["rules"] = new JsonArray(fact.Rules.Select(r => (JsonNode)r).ToArray()),
            ["launch"] = fact.IsLaunch,
        };
        if (fact.Target is { } t)
        {
            o["target"] = new JsonObject { ["code"] = t.Ref.Code, ["level"] = t.Ref.Level.ToString(), ["text"] = t.MatchedText, ["hedged"] = t.Hedged };
        }
        if (fact.RulesetId is { } rulesetId)
        {
            o["rulesetVersion"] = rulesetId; // P08 provenance: which rule-set snapshot resolved the kind
        }
        if (fact.EventKindCode is not null && !indexes.EventKinds.IsEmpty)
        {
            o["eventKindPolicyVersion"] = indexes.EventKinds.PolicyVersion; // the kind came from the rule, not the legacy stamp (P07 provenance kept)
        }
        if (fact.RuleCode is { } ruleCode)
        {
            o["ruleCode"] = ruleCode;
            o["ruleVersion"] = fact.RuleVersion;
            o["eventKindCode"] = fact.EventKindCode;
        }
        o["places"] = new JsonArray(fact.Places.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Place.PlaceId,
            ["name"] = p.Place.Name,
            ["level"] = p.Place.Level.ToString(),
            ["region"] = gazetteer.RegionOf(p.Place)?.Name,
            ["role"] = p.Role.ToString(),
            ["text"] = p.MatchedText,
        }).ToArray());
        if (fact.Direction is { } d)
        {
            o["direction"] = new JsonObject { ["deg"] = d.Degrees, ["kind"] = d.Kind.ToString(), ["text"] = d.Text };
        }
        return JsonDocument.Parse(o.ToJsonString());
    }
}
