using System.Text.RegularExpressions;

namespace Puluj.Domain;

/// <summary>Contract shape of an event kind code (contracts/messaging/schemas/common.schema.json eventKindCode).</summary>
public static partial class EventKindCodes
{
    [GeneratedRegex(@"^[a-z_]+(\.[a-z_]+)+$")]
    private static partial Regex Pattern();

    public static bool IsValid(string code) => Pattern().IsMatch(code);
}
