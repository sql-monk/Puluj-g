using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Puluj.Processing.Llm;

/// <summary>
/// <see cref="ILlmCompletion"/> over the Anthropic SDK: the same system prompt and JSON schema as <see cref="LlmParser"/>,
/// the budget of the request, a refusal reported as such, and every failure mapped to the provider-neutral
/// <see cref="LlmCompletionException"/> (429 → rate_limited retryable; 5xx → provider_error retryable; timeout →
/// provider_timeout retryable; 4xx → provider_error terminal; unparsable answer → invalid_response terminal).
/// </summary>
public sealed class AnthropicCompletion(LlmParser parser, IOptionsMonitor<LlmOptions> options, ILogger<AnthropicCompletion> logger) : ILlmCompletion
{
    private readonly object _clientLock = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var key = string.IsNullOrWhiteSpace(o.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : o.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            throw new LlmCompletionException("no_api_key", "Llm:ApiKey / ANTHROPIC_API_KEY is not configured", retryable: false);
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
        var parameters = LlmParser.CreateParams(request.Model, Math.Clamp(request.MaxOutputTokens, 64, 8192), parser.SystemPrompt, request.Text);
        var requestPayload = LlmParser.RequestPayload(parameters); // verbatim body, audited even when the call fails
        try
        {
            var response = await client.Messages.Create(parameters, cancellationToken: timeout.Token);
            var responsePayload = LlmParser.ResponsePayload(response);
            var usage = response.Usage;
            if (response.StopReason == "refusal")
            {
                logger.LogInformation("LLM declined to classify the message ({RequestId})", response.ID);
                return new LlmCompletionResult(null, true, usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens, response.ID, requestPayload, responsePayload);
            }
            var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));
            try
            {
                using var _ = JsonDocument.Parse(json); // the worker maps it; here only "is it JSON at all"
            }
            catch (JsonException ex)
            {
                throw new LlmCompletionException("invalid_response", "the model did not return JSON: " + ex.Message, retryable: false, inner: ex) { RequestPayload = requestPayload, ResponsePayload = responsePayload };
            }
            return new LlmCompletionResult(json, false, usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens, response.ID, requestPayload, responsePayload);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmCompletionException("rate_limited", LlmParser.ProviderErrorMessage(ex), retryable: true, ex.StatusCode, ex) { RequestPayload = requestPayload, ResponsePayload = LlmParser.ErrorPayload(ex) };
        }
        catch (AnthropicApiException ex)
        {
            var retryable = (int)ex.StatusCode >= 500 || ex.StatusCode == HttpStatusCode.RequestTimeout;
            throw new LlmCompletionException("provider_error", LlmParser.ProviderErrorMessage(ex), retryable, ex.StatusCode, ex) { RequestPayload = requestPayload, ResponsePayload = LlmParser.ErrorPayload(ex) };
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
}
