using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Domain;

/// <summary>
/// The one place that knows how a source code is derived from what identifies the source, so that
/// data/sources.json, the admin API and any import agree. A Telegram channel is identified by its username
/// (case-insensitive on Telegram's side) and gets the code <c>tg_&lt;username lowercase&gt;</c>; a second row for
/// the same username under another code means the collector reads the channel twice (docs/naming.md).
/// </summary>
public static class SourceCodes
{
    public const string TelegramPrefix = "tg_";

    /// <summary>Code for a Telegram channel: <c>tg_</c> + normalized username in lowercase.</summary>
    public static string Telegram(string username) =>
        TelegramPrefix + (NormalizeTelegramUsername(username) ?? throw new ArgumentException("Telegram username is empty", nameof(username))).ToLowerInvariant();

    /// <summary>Accepts "@name", "name", "https://t.me/name" or "t.me/name/123" and returns "name"; null when nothing is left.</summary>
    public static string? NormalizeTelegramUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        var idx = s.IndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            s = s[(idx + 5)..];
        }
        s = s.TrimStart('@').Split('/', '?', '#')[0].Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>The normalized channel username of a Telegram source (config.channel), or null for other sources / no channel.</summary>
    public static string? TelegramChannel(Source source)
    {
        if (source.Type != SourceType.Telegram || source.Config is null
            || !source.Config.RootElement.TryGetProperty("channel", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }
        return NormalizeTelegramUsername(c.GetString());
    }

    /// <summary>Whether two Telegram usernames name the same channel.</summary>
    public static bool SameTelegramChannel(string? a, string? b) =>
        a is not null && b is not null && string.Equals(NormalizeTelegramUsername(a), NormalizeTelegramUsername(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The source that already covers this Telegram channel, whatever its code, or null.</summary>
    public static Source? FindTelegramChannel(IEnumerable<Source> sources, string? username)
    {
        var wanted = NormalizeTelegramUsername(username);
        return wanted is null ? null : sources.FirstOrDefault(s => SameTelegramChannel(TelegramChannel(s), wanted));
    }
}
