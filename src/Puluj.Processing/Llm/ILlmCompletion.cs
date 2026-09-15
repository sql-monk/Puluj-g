using System.Net;

namespace Puluj.Processing.Llm;

/// <summary>What the llm-worker sends to the provider: the normalized text and the budget of the `llm.requested` command.</summary>
public sealed record LlmCompletionRequest(string Text, string Model, string PromptVersion, int MaxOutputTokens, TimeSpan Timeout);

/// <summary>The provider's answer: the JSON of the extraction schema (null when the model refused), usage and its own request id.</summary>
public sealed record LlmCompletionResult(string? ResponseJson, bool Refused, long InputTokens, long CacheCreationInputTokens, long CacheReadInputTokens, long OutputTokens, string? ProviderRequestId);

/// <summary>
/// One call to the model, provider-neutral (plan §6.2: the call is never part of a database transaction). The real
/// implementation is <see cref="AnthropicCompletion"/>; tests use a fake. Failures are <see cref="LlmCompletionException"/>
/// with the same taxonomy for every provider, so the worker's retry/terminal decision is testable without a key.
/// </summary>
public interface ILlmCompletion
{
    Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct);
}

/// <summary>A provider failure: status code when the provider answered, `Retryable` decides between another attempt and a terminal `llm.failed`.</summary>
public sealed class LlmCompletionException(string code, string message, bool retryable, HttpStatusCode? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>provider_timeout | rate_limited | provider_error | invalid_response | no_api_key (the worker adds its own terminal codes: budget_unavailable, deadline_exceeded, normalization_drift, attempts_exhausted)</summary>
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
