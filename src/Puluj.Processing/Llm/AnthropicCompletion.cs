using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Puluj.Processing.Llm;

/// <summary>
/// <see cref="ILlmCompletion"/> over the Anthropic SDK: the system prompt and JSON schema of <see cref="LlmParser"/>,
/// the budget of the request, a refusal reported as such, and every failure mapped to the provider-neutral
/// <see cref="LlmCompletionException"/> (429 → rate_limited retryable; 5xx → provider_error retryable; timeout →
/// provider_timeout retryable; 4xx → provider_error terminal; unparsable answer → invalid_response terminal).
/// </summary>
public sealed class AnthropicCompletion(IOptionsMonitor<LlmOptions> options, ILogger<AnthropicCompletion> logger) : ILlmCompletion
{
    private readonly object _clientLock = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        var key = options.CurrentValue.ApiKeyFor(LlmProvider.Anthropic);
        if (string.IsNullOrEmpty(key))
        {
            throw new LlmCompletionException("no_api_key", "Llm:Anthropic:ApiKey / ANTHROPIC_API_KEY is not configured", retryable: false);
        }
        AnthropicClient client;
        lock (_clientLock)
        {
            if (_client is null || _clientKey != key)
            {
                _client = new AnthropicClient { ApiKey = key };
                _clientKey = key;
            }
            client = _client;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        var parameters = CreateParams(request.Model, Math.Clamp(request.MaxOutputTokens, 64, 8192), request.SystemPrompt, request.Text);
        var requestPayload = JsonSerializer.Serialize(parameters.RawBodyData); // verbatim body, audited even when the call fails
        try
        {
            var response = await client.Messages.Create(parameters, cancellationToken: timeout.Token);
            var responsePayload = JsonSerializer.Serialize(response.RawData);
            var usage = response.Usage;
            if (response.StopReason == "refusal")
            {
                logger.LogInformation("LLM declined to classify the message ({RequestId})", response.ID);
                return new LlmCompletionResult(null, true, usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens, response.ID, requestPayload, responsePayload);
            }
            var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));
            try
            {
                using var _ = JsonDocument.Parse(json); // the caller maps it; here only "is it JSON at all"
            }
            catch (JsonException ex)
            {
                throw new LlmCompletionException("invalid_response", "the model did not return JSON: " + ex.Message, retryable: false, inner: ex) { RequestPayload = requestPayload, ResponsePayload = responsePayload };
            }
            return new LlmCompletionResult(json, false, usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens, response.ID, requestPayload, responsePayload);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmCompletionException("rate_limited", ErrorMessage(ex), retryable: true, ex.StatusCode, ex) { RequestPayload = requestPayload, ResponsePayload = ErrorPayload(ex) };
        }
        catch (AnthropicApiException ex)
        {
            var retryable = (int)ex.StatusCode >= 500 || ex.StatusCode == HttpStatusCode.RequestTimeout;
            throw new LlmCompletionException("provider_error", ErrorMessage(ex), retryable, ex.StatusCode, ex) { RequestPayload = requestPayload, ResponsePayload = ErrorPayload(ex) };
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LlmCompletionException("provider_timeout", $"no response within {request.Timeout}", retryable: true, inner: ex) { RequestPayload = requestPayload };
        }
        catch (Exception ex) when (ex is not LlmCompletionException and not OperationCanceledException)
        {
            throw new LlmCompletionException("provider_error", ex.Message, retryable: true, inner: ex) { RequestPayload = requestPayload };
        }
    }

    /// <summary>The request shape: cached system prompt, low effort, structured JSON output per <see cref="LlmParser.OutputSchema"/>.</summary>
    internal static MessageCreateParams CreateParams(string model, int maxTokens, string systemPrompt, string text) => new()
    {
        Model = model,
        MaxTokens = maxTokens,
        System = new List<TextBlockParam> { new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() } },
        OutputConfig = new OutputConfig
        {
            Effort = Effort.Low,
            Format = new JsonOutputFormat { Schema = LlmParser.OutputSchema() },
        },
        Messages = [new() { Role = Role.User, Content = text }],
    };

    /// <summary>The API's error message out of the response body ({"type":"error","error":{"type":..,"message":..}}), else the exception's.</summary>
    private static string ErrorMessage(AnthropicApiException ex)
    {
        if (string.IsNullOrEmpty(ex.ResponseBody))
        {
            return ex.Message;
        }
        try
        {
            using var doc = JsonDocument.Parse(ex.ResponseBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (message is not null)
                {
                    return type is null ? message : $"{type}: {message}";
                }
            }
        }
        catch (JsonException)
        {
        }
        return ex.Message;
    }

    /// <summary>The error body of a failed call, as JSON when the API returned JSON, else the text wrapped in a JSON string.</summary>
    private static string? ErrorPayload(AnthropicApiException ex) => LlmParser.BodyAsJson(ex.ResponseBody);
}
