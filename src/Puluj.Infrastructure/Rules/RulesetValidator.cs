using System.Text;
using System.Text.Json;

namespace Puluj.Infrastructure.Rules;

/// <summary>
/// Static checks of a rule set before it may be published (plan §8.3 authoring flow: draft → validate → …). Errors block
/// publishing; warnings are reported. Everything here is deterministic and needs no database beyond the known kinds
/// (code → enabled) and source codes.
/// </summary>
public static class RulesetValidator
{
    public const int MinStemLength = 2;
    public const int MaxStemLength = 32;
    public const int MaxWindow = 12;
    public static readonly string[] Languages = [RuleDefinition.AnyLanguage, "uk", "ru", "en"];

    public static ValidationReport Validate(IReadOnlyList<RuleDefinition> rules, IReadOnlyDictionary<string, bool> kindsEnabled, IReadOnlySet<string>? knownSources)
    {
        var issues = new List<ValidationIssue>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var byBehaviour = new Dictionary<string, List<RuleDefinition>>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            void Error(string code, string message) => issues.Add(new ValidationIssue("error", r.RuleCode, code, message));
            void Warn(string code, string message) => issues.Add(new ValidationIssue("warning", r.RuleCode, code, message));

            if (string.IsNullOrWhiteSpace(r.RuleCode) || r.RuleCode.Any(char.IsWhiteSpace))
            {
                Error("rule_code", "rule_code is empty or contains whitespace");
            }
            else if (!codes.Add(r.RuleCode))
            {
                Error("duplicate_rule_code", $"rule_code '{r.RuleCode}' appears more than once");
            }
            if (!kindsEnabled.TryGetValue(r.EventKindCode, out var enabled))
            {
                Error("unknown_kind", $"event kind '{r.EventKindCode}' is not in the catalog");
            }
            else if (!enabled)
            {
                Warn("kind_disabled", $"event kind '{r.EventKindCode}' is disabled: the rule is loaded but never fires until the kind is enabled");
            }
            if (!Languages.Contains(r.LanguageOrAny, StringComparer.Ordinal))
            {
                Error("unknown_language", $"language '{r.Language}' is not one of {string.Join(", ", Languages)}");
            }
            if (r.Sources is { } sources)
            {
                if (sources.Length == 0)
                {
                    Error("empty_source_scope", "sources is an empty list: use null for every source");
                }
                foreach (var s in sources.Where(s => knownSources is not null && !knownSources.Contains(s)))
                {
                    Warn("unknown_source", $"source '{s}' is not configured");
                }
            }
            if (r.PositivePatterns.Count == 0)
            {
                Error("no_positive_patterns", "a rule needs at least one positive pattern");
            }
            foreach (var p in r.PositivePatterns)
            {
                CheckPattern(p, "positive", Error, Warn);
            }
            foreach (var p in r.NegativePatterns ?? [])
            {
                CheckPattern(p, "negative", Error, Warn);
            }
            if (r.PositivePatterns.Count > 0 && (r.NegativePatterns ?? []).Any(n => r.PositivePatterns.Any(p => p.Canonical() == n.Canonical())))
            {
                Error("negation_cancels_positive", "a negative pattern equals a positive one: the rule can never fire");
            }
            if (r.ExtractionHints is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Undefined and not JsonValueKind.Null })
            {
                Error("hints_not_object", "extraction_hints must be a JSON object");
            }
            if (r.ConfidenceModifier is { } cm && (cm < -1 || cm > 1))
            {
                Error("confidence_modifier_range", "confidence_modifier must be within [-1, 1]");
            }
            if (r.ConfidenceModifier is { } cm2 && cm2 != 0)
            {
                Warn("confidence_modifier_unused", "confidence_modifier is stored but not applied by the resolver in this version");
            }
            var key = string.Join("|", r.PositivePatterns.Select(p => p.Canonical()).OrderBy(x => x, StringComparer.Ordinal)) + "#" + r.LanguageOrAny;
            (byBehaviour.TryGetValue(key, out var list) ? list : byBehaviour[key] = []).Add(r);
        }
        // Rules with identical positive patterns and different kinds are only deterministic when their priorities differ.
        foreach (var group in byBehaviour.Values.Where(g => g.Count > 1))
        {
            var kinds = group.Select(g => g.EventKindCode).Distinct(StringComparer.Ordinal).ToList();
            var priorities = group.Select(g => g.Priority).Distinct().ToList();
            if (kinds.Count > 1 && priorities.Count < group.Count)
            {
                issues.Add(new ValidationIssue("error", group[0].RuleCode, "conflicting_rules",
                    $"rules {string.Join(", ", group.Select(g => g.RuleCode))} share positive patterns, name different kinds and tie on priority"));
            }
            else if (kinds.Count == 1)
            {
                issues.Add(new ValidationIssue("warning", group[0].RuleCode, "duplicate_patterns", $"rules {string.Join(", ", group.Select(g => g.RuleCode))} have identical positive patterns"));
            }
        }
        // Equal priorities across different kinds resolve by earliest match then rule_code: allowed, but worth knowing.
        foreach (var tie in rules.GroupBy(r => r.Priority).Where(g => g.Select(x => x.EventKindCode).Distinct().Count() > 1))
        {
            issues.Add(new ValidationIssue("warning", null, "priority_tie", $"priority {tie.Key} is shared by rules of different kinds ({string.Join(", ", tie.Select(r => r.RuleCode))}); ties resolve by earliest match, then rule_code"));
        }
        return ValidationReport.From(issues);
    }

    private static void CheckPattern(PatternDefinition p, string role, Action<string, string> error, Action<string, string> warn)
    {
        if (p.Type != PatternDefinition.StemsType)
        {
            error("unsupported_pattern_type", $"{role} pattern type '{p.Type}' is not supported (only 'stems')");
            return;
        }
        if (p.Stems is null || p.Stems.Length == 0)
        {
            error("empty_pattern", $"{role} pattern has no stems");
            return;
        }
        foreach (var stem in p.Stems)
        {
            if (string.IsNullOrEmpty(stem) || stem.Any(char.IsWhiteSpace))
            {
                error("stem_whitespace", $"{role} stem '{stem}' is empty or contains whitespace (one stem = one token)");
            }
            else if (stem.Length < MinStemLength || stem.Length > MaxStemLength)
            {
                error("stem_length", $"{role} stem '{stem}' must be {MinStemLength}–{MaxStemLength} characters");
            }
            else if (!stem.IsNormalized(NormalizationForm.FormC))
            {
                error("stem_not_nfc", $"{role} stem '{stem}' is not Unicode NFC");
            }
            else if (stem.Any(char.IsUpper))
            {
                error("stem_upper", $"{role} stem '{stem}' has upper-case letters; tokens are lower-case");
            }
            else if (stem.Length == MinStemLength)
            {
                warn("short_stem", $"{role} stem '{stem}' is very short and may match unrelated words");
            }
        }
        if (p.Window is { } w && (w < 1 || w > MaxWindow))
        {
            error("window_range", $"{role} pattern window must be 1–{MaxWindow}");
        }
    }
}
