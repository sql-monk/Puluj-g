using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puluj.Infrastructure.Rules;

/// <summary>
/// One rule as it travels between the seed file, the admin API and the database (plan §8.3, P08). Patterns are ordered
/// stem sequences: every stem must match a token (prefix match, at most 4 inflectional letters beyond the stem), in
/// order, the last one within <see cref="PatternDefinition.Window"/> tokens of the first.
/// </summary>
public sealed record RuleDefinition(
    string RuleCode,
    string EventKindCode,
    string? Language,
    IReadOnlyList<PatternDefinition> PositivePatterns,
    IReadOnlyList<PatternDefinition>? NegativePatterns,
    int Priority,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ExtractionHints = null,
    decimal? ConfidenceModifier = null,
    int? RuleVersion = null,
    bool? Enabled = null)
{
    public const string AnyLanguage = "*";
    public const int DefaultWindow = 5;

    public string LanguageOrAny => string.IsNullOrWhiteSpace(Language) ? AnyLanguage : Language;

    /// <summary>The rule is skipped on a segment that names a target and no alert level (a header line, not a fact) — the legacy AirRaidAlert special case.</summary>
    public bool HeaderIfTargetWithoutLevel =>
        ExtractionHints is { ValueKind: JsonValueKind.Object } h && h.TryGetProperty("header_if_target_without_level", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>What decides whether two versions of a rule behave the same (rule_version stays) or not (rule_version + 1).</summary>
    public string BehaviourKey() => JsonSerializer.Serialize(new
    {
        kind = EventKindCode,
        lang = LanguageOrAny,
        sources = Sources is null ? null : Sources.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        pos = PositivePatterns.Select(p => p.Canonical()).ToArray(),
        neg = (NegativePatterns ?? []).Select(p => p.Canonical()).ToArray(),
        prio = Priority,
        hints = ExtractionHints?.GetRawText(),
        conf = ConfidenceModifier ?? 0m,
        enabled = Enabled ?? true,
    });
}

public sealed record PatternDefinition(string Type, string[] Stems, int? Window = null)
{
    public const string StemsType = "stems";

    public int WindowOrDefault => Window ?? RuleDefinition.DefaultWindow;

    public string Canonical() => $"{Type}:{string.Join(' ', Stems)}:{WindowOrDefault}";
}

/// <summary>A rule-set file: the v1 bootstrap (`data/taxonomy/event-rules.json`) or a sample draft.</summary>
public sealed record RulesetFile(int? RulesetVersion, int? ParentVersion, string? Reason, List<RuleDefinition> Rules);

public sealed record ValidationIssue(string Severity, string? RuleCode, string Code, string Message);

public sealed record ValidationReport(bool Ok, IReadOnlyList<ValidationIssue> Errors, IReadOnlyList<ValidationIssue> Warnings)
{
    public static ValidationReport From(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToList();
        var errors = list.Where(i => i.Severity == "error").ToList();
        return new ValidationReport(errors.Count == 0, errors, list.Where(i => i.Severity == "warning").ToList());
    }
}
