using System.Text.Json;
using Puluj.Domain.Enums;

namespace Puluj.Processing.Indexes;

public sealed record AliasEntry(string Alias, string[] Words, bool Exact, AliasTargetLevel Level, int TargetId,
    int Priority, ConfidenceLevel ImpliedConfidence, string Language, int? SourceId);

/// <summary>Fully resolved position in the hierarchy for an alias target.</summary>
public sealed record TargetRef(int CategoryId, int? ClassId, int? FamilyId, int? ModelId, string Code, string Name, AliasTargetLevel Level);

/// <summary>Class-level behaviour read from TargetClass.Metadata (spec §12, §17).</summary>
public sealed record ClassProfile(int ClassId, string Code, double? SpeedKmhMin, double? SpeedKmhMax, bool EtaEnabled,
    string DisplayMode, int FadeMinutes, int CorrelationWindowMinutes)
{
    public static ClassProfile FromMetadata(int classId, string code, JsonDocument? metadata)
    {
        var m = metadata?.RootElement;
        return new ClassProfile(classId, code,
            GetDouble(m, "speedKmhMin"), GetDouble(m, "speedKmhMax"),
            GetBool(m, "etaEnabled") ?? false,
            GetString(m, "displayMode") ?? "uav",
            (int)(GetDouble(m, "fadeMinutes") ?? 20),
            (int)(GetDouble(m, "correlationWindowMinutes") ?? 30));
    }

    private static double? GetDouble(JsonElement? m, string name) =>
        m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool? GetBool(JsonElement? m, string name) =>
        m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static string? GetString(JsonElement? m, string name) =>
        m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>In-memory snapshot of the target taxonomy. Immutable; replaced wholesale on refresh.</summary>
public sealed class TaxonomyIndex(
    IReadOnlyList<AliasEntry> aliases,
    IReadOnlyDictionary<(AliasTargetLevel, int), TargetRef> refs,
    IReadOnlyDictionary<int, ClassProfile> classProfiles,
    IReadOnlyDictionary<string, int> categoryIdsByCode,
    IReadOnlyDictionary<string, int> classIdsByCode)
{
    public IReadOnlyList<AliasEntry> Aliases { get; } = aliases;

    public TargetRef? Resolve(AliasTargetLevel level, int id) => refs.TryGetValue((level, id), out var r) ? r : null;

    public ClassProfile? ClassProfile(int? classId) => classId is int id && classProfiles.TryGetValue(id, out var p) ? p : null;

    public int? CategoryId(string code) => categoryIdsByCode.TryGetValue(code, out var id) ? id : null;

    private readonly IReadOnlyDictionary<int, string> _categoryCodes = categoryIdsByCode.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Stable taxonomy code of a category id (the `category` of track.changed, P09); the id as text when unknown.</summary>
    public string CategoryCode(int categoryId) => _categoryCodes.TryGetValue(categoryId, out var code) ? code : categoryId.ToString();

    public int? ClassId(string code) => classIdsByCode.TryGetValue(code, out var id) ? id : null;

    public static TaxonomyIndex Empty { get; } = new([], new Dictionary<(AliasTargetLevel, int), TargetRef>(),
        new Dictionary<int, ClassProfile>(), new Dictionary<string, int>(), new Dictionary<string, int>());
}
