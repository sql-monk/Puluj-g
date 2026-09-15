using Puluj.Domain;
using Puluj.Domain.Enums;
using Puluj.Processing.Parsing;
using Puluj.Processing.Text;

namespace Puluj.Processing.Rules;

/// <summary>What one rule set says about one segment; null when no rule fired. The rule code is also the name recorded in <c>rules[]</c> / ParserMetadata (v1 codes equal the legacy <c>event:…</c> names).</summary>
/// <param name="TokenStart">Index of the first matched token.</param>
/// <param name="TokenEnd">Index of the last matched token (inclusive).</param>
public sealed record Resolution(string KindCode, EventType Legacy, string RuleCode, int RuleVersion, int TokenStart, int TokenEnd);

/// <summary>
/// Plan §8.3 (P08). Resolves the event kind of a segment from a pinned <see cref="RulesetIndex"/>: rules in priority
/// order (then rule code), the first whose positive pattern matches and no negative pattern vetoes wins; within a rule
/// the earliest match position. Language and source scope filter rules; a rule with the header hint is skipped on a
/// segment that names a target and no alert level (a "Тривога! Шахеди на …" header, not a fact). The built-in set
/// delegates to the frozen <see cref="EventTypeMatcher"/>, so parity is by construction.
/// </summary>
public static class EventKindResolver
{
    public static Resolution? Resolve(RulesetIndex rules, Segment segment, bool hasTarget, bool hasLevel, string language, string? sourceCode)
    {
        if (rules.IsBuiltin)
        {
            var (type, rule, _) = EventTypeMatcher.Match(segment, hasTarget);
            return rule is null ? null : new Resolution(EventKindLegacyMap.ToCode(type), type, rule, 1, 0, 0);
        }
        var tokens = segment.Tokens;
        // Rules come ordered by priority desc, then code: within one priority the earliest match wins, then the code.
        CompiledRule? best = null;
        (int Start, int End) bestMatch = default;
        int? group = null;
        foreach (var r in rules.Rules)
        {
            if (group is int g && r.Priority != g && best is not null)
            {
                break;
            }
            group = r.Priority;
            if (!r.Fires || !LanguageMatches(r.Language, language) || (r.Sources is { } scope && (sourceCode is null || !scope.Contains(sourceCode))))
            {
                continue;
            }
            if (r.HeaderIfTargetWithoutLevel && hasTarget && !hasLevel)
            {
                continue; // veto this rule only; lower-priority rules may still name the segment (legacy `break` semantics)
            }
            var match = FirstMatch(r.Positive, tokens);
            if (match is null)
            {
                continue;
            }
            if (r.Negative.Count > 0 && FirstMatch(r.Negative, tokens) is not null)
            {
                continue;
            }
            if (best is null || match.Value.Start < bestMatch.Start)
            {
                best = r;
                bestMatch = match.Value;
            }
        }
        return best is null ? null : new Resolution(best.KindCode, best.Legacy, best.Code, best.RuleVersion, bestMatch.Start, bestMatch.End);
    }

    public static bool LanguageMatches(string ruleLanguage, string language) =>
        ruleLanguage == "*" || string.Equals(ruleLanguage, language, StringComparison.Ordinal);

    /// <summary>Earliest position at which any of the patterns matches (patterns tried per position, in order).</summary>
    public static (int Start, int End)? FirstMatch(IReadOnlyList<StemPattern> patterns, IReadOnlyList<Token> tokens)
    {
        (int Start, int End)? best = null;
        foreach (var p in patterns)
        {
            for (var i = 0; i + p.Stems.Length <= tokens.Count; i++)
            {
                if (best is { } b && i >= b.Start)
                {
                    break;
                }
                if (MatchesWithin(p.Stems, tokens, i, p.Window) is int end)
                {
                    best = (i, end);
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>All stems in order, starting at i, with at most `window` tokens between first and last (the legacy matcher's rule); returns the last matched index.</summary>
    public static int? MatchesWithin(string[] stems, IReadOnlyList<Token> tokens, int i, int window)
    {
        if (!StemMatch.Matches(stems[0], tokens[i].Text, exact: false))
        {
            return null;
        }
        var pos = i;
        for (var k = 1; k < stems.Length; k++)
        {
            var found = false;
            for (var j = pos + 1; j < Math.Min(tokens.Count, i + window + 1); j++)
            {
                if (StemMatch.Matches(stems[k], tokens[j].Text, exact: false))
                {
                    pos = j;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return null;
            }
        }
        return pos;
    }
}

/// <summary>The launch flag of a fact ("пуск", "зліт"): a token prefix, independent of the kind rules (frozen legacy semantics).</summary>
public static class LaunchDetector
{
    private static readonly string[] Stems = ["пуск", "зліт", "злет", "запуск", "взлет"];

    public static bool Detect(IReadOnlyList<Token> tokens) => tokens.Any(t => Stems.Any(s => t.Text.StartsWith(s, StringComparison.Ordinal)));
}
