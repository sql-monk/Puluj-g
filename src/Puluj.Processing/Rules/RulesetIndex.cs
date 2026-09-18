using Puluj.Domain;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Rules;

namespace Puluj.Processing.Rules;

/// <summary>One ordered stem sequence: every stem matches a token in order, the last within <see cref="Window"/> tokens of the first.</summary>
public sealed record StemPattern(string[] Stems, int Window);

/// <summary>A rule compiled for matching. <see cref="Fires"/> is false when the rule or its kind is disabled (kept for provenance, never matches).</summary>
public sealed record CompiledRule(
    string Code,
    string KindCode,
    EventType Legacy,
    int Priority,
    int RuleVersion,
    string Language,
    IReadOnlySet<string>? Sources,
    IReadOnlyList<StemPattern> Positive,
    IReadOnlyList<StemPattern> Negative,
    bool HeaderIfTargetWithoutLevel,
    bool Fires);

/// <summary>
/// Plan §8.3 (P08). Immutable snapshot of one rule-set version, ordered for deterministic resolution (priority desc,
/// then rule code). A parser takes one snapshot per message ("ruleset pinned per job"): a refresh never changes the
/// rules in the middle of one message. <see cref="Builtin"/> stands for the frozen <c>EventTypeMatcher</c> phrases
/// until the catalog is seeded (or when it cannot be loaded), and is cited as <c>ruleset_id = builtin</c>.
/// </summary>
public sealed class RulesetIndex
{
    public const string BuiltinId = "builtin";

    public static readonly RulesetIndex Builtin = new(null, BuiltinId, []);

    private RulesetIndex(int? version, string state, IReadOnlyList<CompiledRule> rules)
    {
        Version = version;
        State = state;
        Rules = rules;
    }

    public int? Version { get; }
    public string State { get; }
    /// <summary>The ruleset identifier cited by parser evidence: <c>v3</c> or <c>builtin</c>.</summary>
    public string Id => Version is int v ? $"v{v}" : BuiltinId;
    public bool IsBuiltin => Version is null;
    /// <summary>Priority desc, then rule code asc — the resolution order.</summary>
    public IReadOnlyList<CompiledRule> Rules { get; }

    public static RulesetIndex From(RulesetSnapshotData data)
    {
        var rules = data.Rules
            .Select(r => Compile(r.Definition, r.KindEnabled))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
        return new RulesetIndex(data.Version, data.State, rules);
    }

    /// <summary>An in-memory set (tests, preview of a draft that is not stored yet).</summary>
    public static RulesetIndex FromDefinitions(int? version, string state, IEnumerable<RuleDefinition> definitions, Func<string, bool>? kindEnabled = null)
    {
        var rules = definitions
            .Select(d => Compile(d, kindEnabled?.Invoke(d.EventKindCode) ?? true))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
        return new RulesetIndex(version, state, rules);
    }

    private static CompiledRule Compile(RuleDefinition d, bool kindEnabled) => new(
        d.RuleCode,
        d.EventKindCode,
        EventKindLegacyMap.LegacyOrFallback(d.EventKindCode),
        d.Priority,
        d.RuleVersion ?? 1,
        d.LanguageOrAny,
        d.Sources is null ? null : d.Sources.ToHashSet(StringComparer.Ordinal),
        d.PositivePatterns.Select(p => new StemPattern(p.Stems, p.WindowOrDefault)).ToList(),
        (d.NegativePatterns ?? []).Select(p => new StemPattern(p.Stems, p.WindowOrDefault)).ToList(),
        d.HeaderIfTargetWithoutLevel,
        (d.Enabled ?? true) && kindEnabled);
}
