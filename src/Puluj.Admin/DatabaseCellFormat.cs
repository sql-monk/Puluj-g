using System.Globalization;

namespace Puluj.Admin;

/// <summary>
/// How the table browser and the SQL console print one cell. Timestamps are ISO 8601 and keep their zone: a timestamptz
/// comes from Npgsql as a UTC DateTime and is printed with "Z", where the invariant culture used to print "09/21/2026
/// 17:54:54" with no zone at all next to "+00:00" in the JSON of the same row (admin re-audit R05).
/// </summary>
public static class DatabaseCellFormat
{
    public const int MaxLength = 4_000;

    public static string Format(object value)
    {
        var text = value switch
        {
            DateTime { Kind: DateTimeKind.Utc } d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        // Browsing a JSON payload or long message must not turn an admin request into a multi-megabyte response.
        return text.Length <= MaxLength ? text : $"{text[..MaxLength]}…";
    }
}
