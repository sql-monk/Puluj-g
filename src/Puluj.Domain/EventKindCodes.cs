using System.Text.RegularExpressions;

namespace Puluj.Domain;

/// <summary>Stable dotted shape of an event-kind code.</summary>
public static partial class EventKindCodes
{
    [GeneratedRegex(@"^[a-z_]+(\.[a-z_]+)+$")]
    private static partial Regex Pattern();

    public static bool IsValid(string code) => Pattern().IsMatch(code);
}
