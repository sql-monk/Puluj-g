namespace Puluj.Processing.Llm;

/// <summary>The model backends the fallback can talk to. OpenAI and Ollama share the OpenAI chat-completions wire format.</summary>
public enum LlmProvider
{
    Anthropic,
    OpenAI,
    Ollama,
}

/// <summary>
/// One provider's connection and price card (Llm:Anthropic:*, Llm:OpenAI:*, Llm:Ollama:*). Every provider keeps its own,
/// so all can be configured at once and Llm:Provider only switches between them. The same app_settings keys are read by
/// the processor and the Python entity extractor.
/// </summary>
public sealed class LlmProviderOptions
{
    public string Model { get; set; } = "";
    /// <summary>API key; falls back to ANTHROPIC_API_KEY / OPENAI_API_KEY when empty. Ollama needs none.</summary>
    public string? ApiKey { get; set; }
    /// <summary>OpenAI-compatible endpoint (OpenAI, Ollama); empty = the provider default. Ignored for Anthropic.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>USD per million tokens; stored with every request. An Ollama call is always costed at zero.</summary>
    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
    public decimal CacheWriteUsdPerMillionTokens { get; set; }
    public decimal CacheReadUsdPerMillionTokens { get; set; }
}

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public bool Enabled { get; set; }
    /// <summary>Anthropic | OpenAI | Ollama (case-insensitive): which of the provider sections below is used. A string, not
    /// the enum: a mistyped value in app_settings must not make the whole options object unbindable — it only switches the
    /// fallback off (<see cref="TryGetProvider"/>).</summary>
    public string Provider { get; set; } = nameof(LlmProvider.Anthropic);

    /// <summary>Defaults are Claude Opus 5 list prices.</summary>
    public LlmProviderOptions Anthropic { get; set; } = new()
    {
        Model = "claude-opus-5",
        InputUsdPerMillionTokens = 5m,
        OutputUsdPerMillionTokens = 25m,
        CacheWriteUsdPerMillionTokens = 6.25m,
        CacheReadUsdPerMillionTokens = .5m,
    };
    /// <summary>No default prices: set the chosen model's price card in the admin UI.</summary>
    public LlmProviderOptions OpenAI { get; set; } = new() { Model = "gpt-5-mini" };
    public LlmProviderOptions Ollama { get; set; } = new() { Model = "qwen3:8b" };

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

    public bool TryGetProvider(out LlmProvider provider) =>
        Enum.TryParse(string.IsNullOrWhiteSpace(Provider) ? nameof(LlmProvider.Anthropic) : Provider.Trim(), ignoreCase: true, out provider) && Enum.IsDefined(provider);

    public LlmProviderOptions For(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenAI => OpenAI,
        LlmProvider.Ollama => Ollama,
        _ => Anthropic,
    };

    /// <summary>The section of the selected provider (Anthropic when Provider is not a known name).</summary>
    public LlmProviderOptions Current => For(TryGetProvider(out var provider) ? provider : LlmProvider.Anthropic);

    /// <summary>The key the provider is called with: its own ApiKey, else the provider's environment variable.</summary>
    public string? ApiKeyFor(LlmProvider provider) => ResolveApiKey(provider, For(provider).ApiKey);

    /// <summary>The model id as audited: bare for Anthropic (history stays comparable), "openai/…" / "ollama/…" otherwise.</summary>
    public string AuditModel => TryGetProvider(out var provider) && provider != LlmProvider.Anthropic
        ? $"{provider.ToString().ToLowerInvariant()}/{Current.Model}"
        : Current.Model;

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
