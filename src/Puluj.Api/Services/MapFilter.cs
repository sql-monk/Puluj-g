using Microsoft.AspNetCore.Http;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Api.Services;

public sealed class MapWindowTooLargeException(TimeSpan maximum) : Exception($"The requested replay window exceeds the {maximum.TotalHours:0}-hour maximum.")
{
    public TimeSpan Maximum { get; } = maximum;
}

public sealed class MapFutureHistoryException : Exception
{
    public MapFutureHistoryException() : base("Historical state cannot be requested in the future.") { }
}

/// <summary>
/// The public map's subset of the canonical U03 query.  It deliberately parses the
/// same comma-separated URL values as the catalogue endpoints, so a map URL never
/// falls back to an unfiltered response.
/// </summary>
public sealed class MapFilter
{
    public HashSet<int> CategoryIds { get; } = [];
    public HashSet<int> ClassIds { get; } = [];
    public HashSet<int> FamilyIds { get; } = [];
    public HashSet<int> ModelIds { get; } = [];
    public HashSet<int> SourceIds { get; } = [];
    public HashSet<string> EventKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> EventCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> EntityKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int? RegionId { get; private init; }
    public string? Status { get; private init; }
    public string? Confidence { get; private init; }
    public string? Location { get; private init; }
    public string? Search { get; private init; }
    public bool? HasResults { get; private init; }

    // Any map filter disables the shared live cache: cached data must never leak
    // across two different public URLs.
    public bool IsFiltered => CategoryIds.Count > 0 || ClassIds.Count > 0 || FamilyIds.Count > 0 || ModelIds.Count > 0 || SourceIds.Count > 0 || EventKinds.Count > 0 || EventCategories.Count > 0 || EntityKinds.Count > 0 || RegionId is not null || Status is not null || Confidence is not null || Location is not null || Search is not null || HasResults is not null;

    public static MapFilter From(IQueryCollection query)
    {
        var result = new MapFilter
        {
            RegionId = OneId(query["regionId"]),
            Status = OneText(query["status"]),
            Confidence = OneText(query["confidence"]),
            Location = OneText(query["location"]),
            Search = OneText(query["q"]),
            HasResults = bool.TryParse(OneText(query["hasResults"]), out var hasResults) ? hasResults : null,
        };
        result.CategoryIds.UnionWith(ParseIds(query["categoryIds"]));
        result.ClassIds.UnionWith(ParseIds(query["classIds"]));
        result.FamilyIds.UnionWith(ParseIds(query["familyIds"]));
        result.ModelIds.UnionWith(ParseIds(query["modelIds"]));
        result.SourceIds.UnionWith(ParseIds(query["sourceIds"]));
        result.EventKinds.UnionWith(ParseText(query["eventKinds"]));
        result.EventCategories.UnionWith(ParseText(query["eventCategories"]));
        result.EntityKinds.UnionWith(ParseText(query["entityKinds"]));
        return result;
    }

    public bool Includes(string kind) => EntityKinds.Count == 0 || EntityKinds.Contains(kind);
    public bool AllowsType(int categoryId, int? classId, int? familyId, int? modelId) =>
        (CategoryIds.Count == 0 || CategoryIds.Contains(categoryId)) &&
        (ClassIds.Count == 0 || (classId is int c && ClassIds.Contains(c))) &&
        (FamilyIds.Count == 0 || (familyId is int f && FamilyIds.Contains(f))) &&
        (ModelIds.Count == 0 || (modelId is int m && ModelIds.Contains(m)));
    public bool AllowsSource(IEnumerable<int> sourceIds) => SourceIds.Count == 0 || sourceIds.Any(SourceIds.Contains);
    public bool AllowsTrack(TargetTrack track) => AllowsTrack(track.Status, track.TargetCategoryId, track.TargetClassId, track.TargetFamilyId, track.TargetModelId, track.TrackConfidence);
    public bool AllowsRevision(TargetTrackRevision track) => AllowsTrack(track.Status, track.TargetCategoryId, track.TargetClassId, track.TargetFamilyId, track.TargetModelId, track.TrackConfidence);
    public bool AllowsTrack(TrackStatus status, int categoryId, int? classId, int? familyId, int? modelId, ConfidenceLevel confidence) =>
        Includes("track") && HasResults is not false && AllowsType(categoryId, classId, familyId, modelId) &&
        (Status is null || status.ToString().Equals(Status, StringComparison.OrdinalIgnoreCase)) &&
        (Confidence is null || confidence.ToString().Equals(Confidence, StringComparison.OrdinalIgnoreCase));
    public bool AllowsTarget(Target target, string? eventKindCode, string? eventCategory) =>
        AllowsTarget(target.SourceId, target.TargetCategoryId, target.TargetClassId, target.TargetFamilyId, target.TargetModelId, target.Confidence, eventKindCode, eventCategory);
    public bool AllowsTarget(int sourceId, int? categoryId, int? classId, int? familyId, int? modelId, ConfidenceLevel confidence, string? eventKindCode, string? eventCategory) =>
        Includes("observation") && HasResults is not false && AllowsSource([sourceId]) &&
        (categoryId is not int category || AllowsType(category, classId, familyId, modelId)) &&
        (EventKinds.Count == 0 || (eventKindCode is not null && EventKinds.Contains(eventKindCode))) &&
        (EventCategories.Count == 0 || (eventCategory is not null && EventCategories.Contains(eventCategory))) &&
        (Confidence is null || confidence.ToString().Equals(Confidence, StringComparison.OrdinalIgnoreCase));
    public bool AllowsAlert(AirAlert alert) => Includes("alert") && HasResults is not false && AllowsSource([alert.SourceId]);
    public bool AllowsPlace(int? placeId, ReferenceCache refs) => RegionId is null || (placeId is int id && (id == RegionId || refs.Ancestors(id).Contains(RegionId.Value)));
    public bool AllowsLocation(bool known) => Location is null || (Location.Equals("known", StringComparison.OrdinalIgnoreCase) ? known : Location.Equals("missing", StringComparison.OrdinalIgnoreCase) && !known);
    public bool AllowsText(params string?[] values) => Search is null || values.Any(value => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true);

    private static HashSet<int> ParseIds(Microsoft.Extensions.Primitives.StringValues values) => values
        .SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(x => int.TryParse(x, out var id) ? id : 0).Where(x => x > 0).ToHashSet();
    private static HashSet<string> ParseText(Microsoft.Extensions.Primitives.StringValues values) => values
        .SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static int? OneId(Microsoft.Extensions.Primitives.StringValues values) => int.TryParse(values.FirstOrDefault(), out var id) && id > 0 ? id : null;
    private static string? OneText(Microsoft.Extensions.Primitives.StringValues values) => values.FirstOrDefault() is { Length: > 0 } value ? value.Trim() : null;
}
