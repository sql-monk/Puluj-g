namespace Puluj.Collectors.Telegram;

public sealed class TelegramOptions
{
    public const string Section = "Collectors:Telegram";
    public bool Enabled { get; set; }
    public int ApiId { get; set; }
    public string? ApiHash { get; set; }
    /// <summary>Phone number of the account used for reading channels, international format.</summary>
    public string? Phone { get; set; }
    /// <summary>2FA password, if the account has one.</summary>
    public string? Password { get; set; }
    /// <summary>One-time login code; alternatively drop it into &lt;SessionPath&gt;.code while the worker waits.</summary>
    public string? VerificationCode { get; set; }
    public string SessionPath { get; set; } = "session/puluj.session";
    /// <summary>Join public channels automatically so live updates arrive (history is readable without joining).</summary>
    public bool AutoJoin { get; set; } = true;
    /// <summary>How many recent messages per channel to backfill on first start.</summary>
    public int BackfillLimit { get; set; } = 30;
    /// <summary>
    /// When set, every channel's whole history from this instant on is loaded once (resumable, cursor in
    /// collector_states), processing is held meanwhile, and when the last channel is done everything derived is
    /// rebuilt from the raw messages in publication order (ReprocessService). Null = only the recent backfill.
    /// </summary>
    public DateTimeOffset? BackfillSince { get; set; }
    /// <summary>Maximum concurrent scheduler workers; history RPC itself remains globally paced.</summary>
    public int HistoryWorkers { get; set; } = 2;
    public TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HistoryRequestInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HistoryMinimumInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HistoryMaximumInterval { get; set; } = TimeSpan.FromSeconds(8);
}
