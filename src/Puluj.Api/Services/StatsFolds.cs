using Puluj.Contracts;
using Puluj.Domain.Enums;

namespace Puluj.Api.Services;

/// <summary>
/// The pure folds of the statistics tabs: the fixed category order, places into regions with a top-N and an "other"
/// row, place pairs into region pairs, and enum counts into labelled slices.
/// </summary>
public static class StatsFolds
{
    /// <summary>The order categories take in every per-category array — the client's palette is keyed the same way.</summary>
    public static readonly string[] CategoryOrder = ["UAV", "MISSILE", "GUIDED_BOMB", "AIRCRAFT", "UNKNOWN"];

    public const string OtherName = "Інші";

    /// <summary>Position of a category code in <see cref="CategoryOrder"/>; anything unknown (or null) counts as UNKNOWN.</summary>
    public static int CategoryIndex(string? code)
    {
        var i = Array.IndexOf(CategoryOrder, code ?? "");
        return i < 0 ? CategoryOrder.Length - 1 : i;
    }

    /// <summary>
    /// Counts per place folded into their region, the top rows kept and the rest summed into one "other" row; the
    /// second value is what did not resolve to a region at all.
    /// </summary>
    public static (List<StatsRegionDto> Rows, long Unlocated) ByRegion(IEnumerable<(int? PlaceId, long Count)> rows, Func<int?, ReferenceCache.PlaceInfo?> regionOf, int top)
    {
        var byRegion = new Dictionary<int, (string Name, long Count)>();
        long unlocated = 0;
        foreach (var (placeId, count) in rows)
        {
            if (regionOf(placeId) is not { } region)
            {
                unlocated += count;
                continue;
            }
            byRegion[region.Id] = (region.Name, byRegion.GetValueOrDefault(region.Id).Count + count);
        }
        var ordered = byRegion.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.Name).ToList();
        var result = ordered.Take(top).Select(kv => new StatsRegionDto(kv.Key, kv.Value.Name, kv.Value.Count)).ToList();
        var rest = ordered.Skip(top).Sum(kv => kv.Value.Count);
        if (rest > 0)
        {
            result.Add(new StatsRegionDto(null, OtherName, rest));
        }
        return (result, unlocated);
    }

    /// <summary>Origin → destination pairs folded into region pairs, most frequent first, at most `cap` rows.</summary>
    public static List<StatsRouteDto> Routes(IEnumerable<(int OriginPlaceId, int DestinationPlaceId, long Count)> rows, Func<int?, ReferenceCache.PlaceInfo?> regionOf, int cap)
    {
        var pairs = new Dictionary<(int, int), (string From, string To, long Count)>();
        foreach (var (origin, destination, count) in rows)
        {
            if (regionOf(origin) is not { } a || regionOf(destination) is not { } b)
            {
                continue;
            }
            var key = (a.Id, b.Id);
            pairs[key] = (a.Name, b.Name, pairs.GetValueOrDefault(key).Count + count);
        }
        return pairs.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.From).ThenBy(kv => kv.Value.To)
            .Take(cap)
            .Select(kv => new StatsRouteDto(kv.Key.Item1, kv.Value.From, kv.Key.Item2, kv.Value.To, kv.Value.Count))
            .ToList();
    }

    public static readonly IReadOnlyDictionary<EventType, string> EventTypeLabels = new Dictionary<EventType, string>
    {
        [EventType.TargetObserved] = "ціль зафіксовано",
        [EventType.AirRaidAlert] = "повітряна тривога",
        [EventType.AlertCancelled] = "відбій тривоги",
        [EventType.TargetCancelled] = "ціль минула",
        [EventType.ExplosionReport] = "вибухи",
        [EventType.AirDefenseActivity] = "робота ППО",
        [EventType.Unknown] = "невідомо",
    };

    public static readonly IReadOnlyDictionary<IdentificationMethod, string> MethodLabels = new Dictionary<IdentificationMethod, string>
    {
        [IdentificationMethod.Structured] = "структуроване джерело",
        [IdentificationMethod.Rule] = "правила",
        [IdentificationMethod.Llm] = "мовна модель",
        [IdentificationMethod.Manual] = "вручну",
    };

    public static readonly IReadOnlyDictionary<ConfidenceLevel, string> ConfidenceLabels = new Dictionary<ConfidenceLevel, string>
    {
        [ConfidenceLevel.Unknown] = "невідомо",
        [ConfidenceLevel.Low] = "низька",
        [ConfidenceLevel.Medium] = "середня",
        [ConfidenceLevel.High] = "висока",
        [ConfidenceLevel.Confirmed] = "підтверджено",
    };

    public static readonly IReadOnlyDictionary<LocationKind, string> LocationKindLabels = new Dictionary<LocationKind, string>
    {
        [LocationKind.Unknown] = "без локації",
        [LocationKind.DirectionOnly] = "лише напрямок",
        [LocationKind.Region] = "область",
        [LocationKind.District] = "район",
        [LocationKind.City] = "населений пункт",
        [LocationKind.Area] = "акваторія / зона",
        [LocationKind.Point] = "точка",
    };

    /// <summary>Counts per enum value as slices, in enum order, zero values dropped.</summary>
    public static List<StatsSliceDto> Slices<T>(IEnumerable<(int Value, long Count)> rows, IReadOnlyDictionary<T, string> labels) where T : struct, Enum
    {
        var counts = rows.GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        return Enum.GetValues<T>()
            .Select(v => new StatsSliceDto(v.ToString(), labels.GetValueOrDefault(v, v.ToString()), counts.GetValueOrDefault(Convert.ToInt32(v))))
            .Where(s => s.Count > 0)
            .ToList();
    }
}
