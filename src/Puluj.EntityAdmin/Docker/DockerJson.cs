using System.Globalization;
using System.Text.Json;

namespace Puluj.EntityAdmin.Docker;

/// <summary>One line of `docker ps --format '{{json .}}'`: the fields the panel uses.</summary>
public sealed record PsRow(string Id, string Name, string Image, string State, string Status, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedAt)
{
    public string? Label(string key) => Labels.TryGetValue(key, out var v) ? v : null;
}
/// <summary>One line of `docker stats --no-stream --format '{{json .}}'`.</summary>
public sealed record StatsRow(string Id, string Name, double? CpuPercent, long? MemoryBytes, long? MemoryLimitBytes);

/// <summary>
/// Parsers of the CLI's JSON output (one object per line). The CLI prints numbers as human-readable strings
/// ("1.23%", "196.5MiB / 31.16GiB", "2026-09-15 06:02:24 +0300 EEST"); every parser tolerates a missing or odd field.
/// </summary>
public static class DockerJson
{
    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1, ["kB"] = 1e3, ["MB"] = 1e6, ["GB"] = 1e9, ["TB"] = 1e12,
        ["KiB"] = 1024, ["MiB"] = 1024d * 1024, ["GiB"] = 1024d * 1024 * 1024, ["TiB"] = 1024d * 1024 * 1024 * 1024,
    };

    public static List<T> ParseLines<T>(string output, Func<string, T?> parse) where T : class
    {
        var list = new List<T>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (parse(line) is { } row)
            {
                list.Add(row);
            }
        }
        return list;
    }

    public static PsRow? ParsePs(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            var id = Str(o, "ID");
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return new PsRow(id, Str(o, "Names"), Str(o, "Image"), Str(o, "State"), Str(o, "Status"), ParseLabels(Str(o, "Labels")), ParseCreatedAt(Str(o, "CreatedAt")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static StatsRow? ParseStats(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            var id = Str(o, "ID");
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            var (used, limit) = ParseMemUsage(Str(o, "MemUsage"));
            return new StatsRow(id, Str(o, "Name"), ParsePercent(Str(o, "CPUPerc")), used, limit);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"k=v,k2=v2" as the CLI prints it. Values may contain '=' (config paths) but not ','.</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return result;
    }

    /// <summary>"1.23%" → 1.23; "--" or garbage → null.</summary>
    public static double? ParsePercent(string text)
    {
        var t = text.Trim().TrimEnd('%');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>"196.5MiB" → bytes; "0B" → 0; anything else → null.</summary>
    public static long? ParseSize(string text)
    {
        var t = text.Trim();
        var i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.'))
        {
            i++;
        }
        if (i == 0 || !double.TryParse(t[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }
        var unit = t[i..].Trim();
        if (unit.Length == 0)
        {
            unit = "B";
        }
        return Units.TryGetValue(unit, out var factor) ? (long)Math.Round(number * factor) : null;
    }

    /// <summary>"196.5MiB / 31.16GiB" → (used, limit).</summary>
    public static (long? Used, long? Limit) ParseMemUsage(string text)
    {
        var parts = text.Split('/');
        return (parts.Length > 0 ? ParseSize(parts[0]) : null, parts.Length > 1 ? ParseSize(parts[1]) : null);
    }

    /// <summary>"2026-09-15 06:02:24 +0300 EEST": the zone abbreviation is dropped, the numeric offset is what counts.</summary>
    public static DateTimeOffset? ParseCreatedAt(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }
        var s = $"{parts[0]} {parts[1]} {parts[2]}";
        return DateTimeOffset.TryParseExact(s, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    private static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
