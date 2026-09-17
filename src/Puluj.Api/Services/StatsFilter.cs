using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Puluj.Contracts;
using Puluj.Domain;
using Puluj.Domain.Enums;

namespace Puluj.Api.Services;

/// <summary>
/// Typed U01 filters as used by the public statistics read side.  The generated SQL fragments contain only parsed
/// integer identifiers; timestamps and values from the database remain parameterised by EF.  Keeping the predicate
/// here prevents one card from quietly using a different population than another.
/// </summary>
public sealed class StatsFilter
{
    private static readonly string[] TargetKeys = ["eventKinds", "eventCategories", "categoryIds", "classIds", "familyIds", "modelIds", "sourceIds", "regionId"];
    private readonly Dictionary<string, string> _requested;
    private readonly HashSet<int> _eventKinds;
    private readonly HashSet<int> _eventCategories;
    private readonly HashSet<int> _legacyKinds;
    private readonly HashSet<int> _legacyCategories;

    private StatsFilter(Dictionary<string, string> requested, HashSet<int> eventKinds, HashSet<int> eventCategories, HashSet<int> legacyKinds, HashSet<int> legacyCategories,
        HashSet<int> categoryIds, HashSet<int> classIds, HashSet<int> familyIds, HashSet<int> modelIds, HashSet<int> sourceIds, HashSet<int> regionPlaces, HashSet<int> alertPlaces)
    {
        _requested = requested;
        _eventKinds = eventKinds;
        _eventCategories = eventCategories;
        _legacyKinds = legacyKinds;
        _legacyCategories = legacyCategories;
        CategoryIds = categoryIds;
        ClassIds = classIds;
        FamilyIds = familyIds;
        ModelIds = modelIds;
        SourceIds = sourceIds;
        RegionPlaces = regionPlaces;
        AlertPlaces = alertPlaces;
    }

    public HashSet<int> CategoryIds { get; }
    public HashSet<int> ClassIds { get; }
    public HashSet<int> FamilyIds { get; }
    public HashSet<int> ModelIds { get; }
    public HashSet<int> SourceIds { get; }
    public HashSet<int> RegionPlaces { get; }
    public HashSet<int> AlertPlaces { get; }

    public bool HasTargetFilter => TargetKeys.Any(_requested.ContainsKey);
    public bool HasResultDerivedFilter => TargetKeys.Any(k => k is not "sourceIds" && _requested.ContainsKey(k));
    public string CacheKey => string.Join("&", _requested.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));

    public static StatsFilter From(IQueryCollection query, ReferenceCache refs)
    {
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in TargetKeys.Concat(new[] { "entityKinds", "q", "status", "confidence", "location", "hasResults", "outcome", "sort", "cursor", "pageSize", "dataset" }))
        {
            var value = Canonical(query[key]);
            if (value is not null) requested[key] = value;
        }
        var kindCodes = Texts(query["eventKinds"]);
        var categoryCodes = Texts(query["eventCategories"]);
        var kindRows = refs.EventKinds.Values.ToList();
        var kinds = kindRows.Where(x => kindCodes.Contains(x.Code, StringComparer.OrdinalIgnoreCase)).Select(x => x.EventKindId).ToHashSet();
        var categories = kindRows.Where(x => categoryCodes.Contains(x.Category.ToString(), StringComparer.OrdinalIgnoreCase)).Select(x => x.EventKindId).ToHashSet();
        var legacyKinds = kindCodes.Select(EventKindLegacyMap.ToEventType).Where(x => x is not null).Select(x => (int)x!.Value).ToHashSet();
        var legacyCategories = kindRows.Where(x => categoryCodes.Contains(x.Category.ToString(), StringComparer.OrdinalIgnoreCase))
            .Select(x => EventKindLegacyMap.ToEventType(x.Code)).Where(x => x is not null).Select(x => (int)x!.Value).ToHashSet();
        var regionId = OneId(query["regionId"]);
        var regions = regionId is int region ? refs.Descendants(region).Append(region).ToHashSet() : [];
        // An alert concerning a selected child may legitimately live on an ancestor; inverse coverage is deliberate.
        var alerts = regionId is int place ? refs.Related(place).ToHashSet() : [];
        return new StatsFilter(requested, kinds, categories, legacyKinds, legacyCategories,
            Ids(query["categoryIds"]), Ids(query["classIds"]), Ids(query["familyIds"]), Ids(query["modelIds"]), Ids(query["sourceIds"]), regions, alerts);
    }

    /// <summary>A single target-evidence predicate.  Catalog kinds are primary, with the documented legacy enum fallback only for rows not backfilled yet.</summary>
    public string TargetPredicate(string alias, bool canonicalFacts, bool includeSource = true)
    {
        var clauses = new List<string>();
        if (canonicalFacts) clauses.Add($"{alias}.duplicate_of_target_id IS NULL");
        if (includeSource && Requested("sourceIds")) clauses.Add(In(alias + ".source_id", SourceIds));
        if (Requested("eventKinds")) clauses.Add(CatalogOrLegacy(alias, _eventKinds, _legacyKinds));
        if (Requested("eventCategories")) clauses.Add(CatalogOrLegacy(alias, _eventCategories, _legacyCategories));
        if (Requested("categoryIds")) clauses.Add(In(alias + ".target_category_id", CategoryIds));
        if (Requested("classIds")) clauses.Add(In(alias + ".target_class_id", ClassIds));
        if (Requested("familyIds")) clauses.Add(In(alias + ".target_family_id", FamilyIds));
        if (Requested("modelIds")) clauses.Add(In(alias + ".target_model_id", ModelIds));
        if (Requested("regionId")) clauses.Add(In(alias + ".location_place_id", RegionPlaces));
        return clauses.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", clauses);
    }

    /// <summary>Tracks are included only when their opening record has matching evidence; it never multiplies tracks.</summary>
    public string TrackPredicate(string trackAlias) => !HasTargetFilter ? string.Empty :
        $" AND EXISTS (SELECT 1 FROM track_targets tx JOIN targets t ON t.target_id = tx.target_id WHERE tx.target_track_id = {trackAlias}.target_track_id{TargetPredicate("t", true)})";

    /// <summary>Raw-message counters retain their published-at clock; result-derived filters use EXISTS, so a multi-fact message is counted once.</summary>
    public string RawPredicate(string rawAlias)
    {
        var clauses = new List<string>();
        if (Requested("sourceIds")) clauses.Add(In(rawAlias + ".source_id", SourceIds));
        if (HasResultDerivedFilter) clauses.Add($"EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = {rawAlias}.raw_message_id{TargetPredicate("t", true, false)})");
        return clauses.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", clauses);
    }

    public string AlertPredicate(string alias)
    {
        var clauses = new List<string>();
        if (Requested("sourceIds")) clauses.Add(In(alias + ".source_id", SourceIds));
        if (Requested("regionId")) clauses.Add(In(alias + ".place_id", AlertPlaces));
        return clauses.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", clauses);
    }

    public StatsFilterMetaDto Meta(string section) => section switch
    {
        "targets" => Metadata(section, TargetKeys, "observedAt", "канонічні факти без повторів; треки за firstSeenAt", null),
        "alerts" => Metadata(section, ["sourceIds", "regionId"], "interval overlap", "обласні або Київські інтервали тривог", null),
        "sources" => Metadata(section, TargetKeys, "publishedAt", "ревізії сирих повідомлень; факти — окремі канонічні результати", HasResultDerivedFilter ? "Повідомлення без відповідного факту виключено з фільтрованої вибірки." : null),
        _ => Metadata(section, TargetKeys, "publishedAt / observedAt", "сирі повідомлення та їхні канонічні результати", HasResultDerivedFilter ? "Повідомлення без відповідного факту виключено з фільтрованої вибірки." : null),
    };

    private StatsFilterMetaDto Metadata(string section, IReadOnlyCollection<string> supported, string timeBasis, string population, string? exclusion)
    {
        var applied = _requested.Where(x => supported.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}").ToList();
        var unavailable = _requested.Where(x => !supported.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new StatsUnavailableFilterDto(x.Key, section == "alerts" && TargetKeys.Contains(x.Key)
                ? "Цей фільтр описує факти, а не інтервали тривог."
                : "Цей фільтр не визначає популяцію цієї метрики."))
            .ToList();
        return new StatsFilterMetaDto(applied, unavailable, timeBasis, population, exclusion);
    }

    private bool Requested(string key) => _requested.ContainsKey(key);
    private static string CatalogOrLegacy(string alias, HashSet<int> kinds, HashSet<int> legacy) =>
        kinds.Count == 0 && legacy.Count == 0 ? "FALSE" : $"({In(alias + ".event_kind_id", kinds)} OR ({alias}.event_kind_id IS NULL AND {In(alias + ".event_type", legacy)}))";
    private static string In(string column, IEnumerable<int> values)
    {
        var ids = values.Distinct().Order().ToList();
        return ids.Count == 0 ? "FALSE" : $"{column} IN ({string.Join(',', ids)})";
    }
    private static HashSet<int> Ids(StringValues values) => values.SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(x => int.TryParse(x, out var id) ? id : 0).Where(x => x > 0).ToHashSet();
    private static int? OneId(StringValues values) => int.TryParse(values.FirstOrDefault(), out var id) && id > 0 ? id : null;
    private static string[] Texts(StringValues values) => values.SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
    private static string? Canonical(StringValues values)
    {
        var parts = values.SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }
}
