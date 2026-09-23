namespace Puluj.Processing.Llm;

/// <summary>The model backends the fallback can talk to. OpenAI and Ollama share the OpenAI chat-completions wire format.</summary>
public enum LlmProvider
{
    Anthropic,
    OpenAI,
    Ollama,
}

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public bool Enabled { get; set; }
    /// <summary>Anthropic | OpenAI | Ollama (case-insensitive). A string, not the enum: a mistyped value in app_settings
    /// must not make the whole options object unbindable — it only switches the fallback off (<see cref="TryGetProvider"/>).</summary>
    public string Provider { get; set; } = nameof(LlmProvider.Anthropic);
    /// <summary>Model id of the chosen provider (claude-opus-5, gpt-5-mini, llama3.1:8b, ...).</summary>
    public string Model { get; set; } = "claude-opus-5";
    /// <summary>API key; falls back to ANTHROPIC_API_KEY / OPENAI_API_KEY (per provider) when empty. Ollama needs none.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Endpoint of an OpenAI-compatible API; empty = https://api.openai.com/v1 (OpenAI) or http://localhost:11434/v1 (Ollama).
    /// Ignored for Anthropic.</summary>
    public string? BaseUrl { get; set; }
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

    /// <summary>Maximum provider attempts for one request.</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>llm-worker lease per request (ADR-0004 W8): another replica takes the job over once it expires; keep it above the provider timeout.</summary>
    public int LeaseSeconds { get; set; } = 90;
    /// <summary>Default `budget.max_output_tokens` of a request when the command carries none.</summary>
    public int MaxOutputTokens { get; set; } = 1024;
    /// <summary>USD per million tokens. These defaults are Claude Opus 5 list prices; store the snapshot with every request.
    /// Set them to the chosen provider's prices; an Ollama call is always costed at zero.</summary>
    public decimal InputUsdPerMillionTokens { get; set; } = 5m;
    public decimal OutputUsdPerMillionTokens { get; set; } = 25m;
    public decimal CacheWriteUsdPerMillionTokens { get; set; } = 6.25m;
    public decimal CacheReadUsdPerMillionTokens { get; set; } = .5m;

    public bool TryGetProvider(out LlmProvider provider) =>
        Enum.TryParse(string.IsNullOrWhiteSpace(Provider) ? nameof(LlmProvider.Anthropic) : Provider.Trim(), ignoreCase: true, out provider) && Enum.IsDefined(provider);

    /// <summary>The key the provider is called with: <see cref="ApiKey"/>, else the provider's environment variable.</summary>
    public static string? ResolveApiKey(LlmProvider provider, string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) ? apiKey
        : ApiKeyVariable(provider) is { } variable ? Environment.GetEnvironmentVariable(variable)
        : null;

    /// <summary>ANTHROPIC_API_KEY / OPENAI_API_KEY; null for Ollama, which is called without a key.</summary>
    public static string? ApiKeyVariable(LlmProvider provider) => provider switch
    {
        LlmProvider.Anthropic => "ANTHROPIC_API_KEY",
        LlmProvider.OpenAI => "OPENAI_API_KEY",
        _ => null,
    };

    public static bool RequiresApiKey(LlmProvider provider) => provider != LlmProvider.Ollama;

    public static string DefaultBaseUrl(LlmProvider provider) => provider switch
    {
        LlmProvider.Ollama => "http://localhost:11434/v1",
        _ => "https://api.openai.com/v1",
    };
}
