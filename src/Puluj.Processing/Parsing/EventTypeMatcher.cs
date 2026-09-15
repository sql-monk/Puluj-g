using Puluj.Domain.Enums;
using Puluj.Processing.Text;

namespace Puluj.Processing.Parsing;

/// <summary>
/// Spec §11 event types from key phrases. Target presence is decided elsewhere.
/// FROZEN since P08: the phrases live in the database as rule-set v1 (`data/taxonomy/event-rules.json`); this class is
/// the built-in fallback before the catalog is seeded and the parity oracle of <c>RulesetParityTests</c>. Do not edit
/// the phrases here — author a new rule-set version instead.
/// </summary>
public static class EventTypeMatcher
{
    private static readonly (string[] Phrase, EventType Type)[] Phrases =
    [
        (["відбій", "тривог"], EventType.AlertCancelled),
        (["відбій", "повітрян"], EventType.AlertCancelled),
        (["отбой", "тревог"], EventType.AlertCancelled),
        (["відбій"], EventType.AlertCancelled),
        (["загроз", "минул"], EventType.TargetCancelled),
        (["загрозу", "знято"], EventType.TargetCancelled),
        (["відбій", "загроз"], EventType.TargetCancelled),
        (["загроз", "відсутн"], EventType.TargetCancelled),
        (["не", "фіксу"], EventType.TargetCancelled),
        (["чисто"], EventType.TargetCancelled),
        (["повітрян", "тривог"], EventType.AirRaidAlert),
        (["оголошен", "тривог"], EventType.AirRaidAlert),
        (["воздушн", "тревог"], EventType.AirRaidAlert),
        (["тривога"], EventType.AirRaidAlert),
        (["робот", "ппо"], EventType.AirDefenseActivity),
        (["працю", "ппо"], EventType.AirDefenseActivity),
        (["ппо", "працю"], EventType.AirDefenseActivity),
        (["сил", "ппо"], EventType.AirDefenseActivity),
        (["збит"], EventType.AirDefenseActivity),
        (["вибух"], EventType.ExplosionReport),
        (["взрыв"], EventType.ExplosionReport),
        (["прильот"], EventType.ExplosionReport),
        (["приліт"], EventType.ExplosionReport),
        (["влучанн"], EventType.ExplosionReport),
    ];

    /// <summary>All stems in order, starting at i, with at most `window` tokens between first and last ("загроза ... минула").</summary>
    private static bool MatchesWithin(string[] stems, IReadOnlyList<Token> tokens, int i, int window)
    {
        if (!StemMatch.Matches(stems[0], tokens[i].Text, exact: false))
        {
            return false;
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
                return false;
            }
        }
        return true;
    }

    private static readonly string[] LaunchStems = ["пуск", "зліт", "злет", "запуск", "взлет"];

    public static (EventType Type, string? Rule, bool Launch) Match(Segment segment, bool hasTarget)
    {
        var tokens = segment.Tokens;
        // "повітряна тривога, жовтий рівень: дронова загроза" is an alert whose cause is named, not a sighting.
        var hasLevel = AlertLevelExtractor.Extract(segment) != AirAlertLevel.Unknown;
        var launch = tokens.Any(t => LaunchStems.Any(s => t.Text.StartsWith(s, StringComparison.Ordinal)));
        foreach (var (phrase, type) in Phrases)
        {
            for (var i = 0; i + phrase.Length <= tokens.Count; i++)
            {
                if (!MatchesWithin(phrase, tokens, i, window: 5))
                {
                    continue;
                }
                // "тривога" alone in a sentence that also names a target is a header, not the fact.
                if (type == EventType.AirRaidAlert && hasTarget && !hasLevel)
                {
                    break;
                }
                return (type, "event:" + string.Join('_', phrase), launch);
            }
        }
        return (hasTarget ? EventType.TargetObserved : EventType.Unknown, null, launch);
    }
}
