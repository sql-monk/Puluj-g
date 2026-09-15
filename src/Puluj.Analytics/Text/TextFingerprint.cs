namespace Puluj.Analytics.Text;

/// <summary>Canonical text and shingles retained as diagnostic evidence for an event pair; they never choose candidates.</summary>
public sealed record TextFingerprint(string Canonical, HashSet<ulong> Shingles)
{
    public bool Indexed => Canonical.Length > 0;

    /// <summary><paramref name="minLength"/> is retained for configuration compatibility; every non-empty text can be diagnostic evidence.</summary>
    public static TextFingerprint Of(string? raw, int minLength)
    {
        var canonical = TextNormalizer.Canonical(raw);
        var shingles = Shingler.Shingles(canonical);
        return new TextFingerprint(canonical, shingles);
    }
}
