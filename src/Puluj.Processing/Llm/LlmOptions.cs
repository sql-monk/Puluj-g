namespace Puluj.Processing.Llm;

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public bool Enabled { get; set; }
    /// <summary>Anthropic model id.</summary>
    public string Model { get; set; } = "claude-opus-5";
    /// <summary>API key; falls back to the ANTHROPIC_API_KEY environment variable when empty.</summary>
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Cap on LLM calls per minute; messages beyond it fall back to rule results only.</summary>
    public int MaxCallsPerMinute { get; set; } = 20;
    /// <summary>
    /// Messages published longer ago than this are not sent to the model: the fallback serves the live picture, and a
    /// rebuild of years of history would otherwise mean tens of thousands of paid calls at seconds each. 0 = no limit.
    /// </summary>
    public double MaxMessageAgeHours { get; set; } = 72;
    /// <summary>How long the model is left alone after a failure that another call would only repeat (a rejected request,
    /// a bad key, an exhausted balance). A rate limit pauses it for one minute regardless.</summary>
    public TimeSpan FailurePause { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Bumped whenever the prompt changes; stored with every LLM-derived target.</summary>
    public string PromptVersion { get; set; } = "1";
    /// <summary>USD per million tokens. These defaults are Claude Opus 5 list prices; store the snapshot with every request.</summary>
    public decimal InputUsdPerMillionTokens { get; set; } = 5m;
    public decimal OutputUsdPerMillionTokens { get; set; } = 25m;
    public decimal CacheWriteUsdPerMillionTokens { get; set; } = 6.25m;
    public decimal CacheReadUsdPerMillionTokens { get; set; } = .5m;
}
