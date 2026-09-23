using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Puluj.Processing.Llm;

/// <summary>
/// <see cref="ILlmCompletion"/> over the OpenAI chat-completions API (POST {BaseUrl}/chat/completions), which OpenAI and
/// Ollama (its /v1 endpoint) both speak: the system prompt and user text as two messages, the answer constrained by
/// <c>response_format: json_schema</c> (strict) to <see cref="LlmParser.OutputSchema"/>. Failures follow the taxonomy of
/// <see cref="AnthropicCompletion"/>: 429 → rate_limited retryable; 5xx/408 → provider_error retryable; timeout →
/// provider_timeout retryable; other 4xx → provider_error terminal; an unreachable server → provider_error retryable;
/// an answer that is not JSON → invalid_response terminal. A model refusal (OpenAI's <c>message.refusal</c>) is reported as such.
/// </summary>
public sealed class OpenAiCompatibleCompletion(IOptionsMonitor<LlmOptions> options, ILogger<OpenAiCompatibleCompletion> logger, HttpMessageHandler? handler = null) : ILlmCompletion, IDisposable
{
    // One client for the process: the timeout is per call (linked token), the key and endpoint per request.
    // The handler is a test seam; DI leaves it null.
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var provider = o.TryGetProvider(out var p) && p != LlmProvider.Anthropic ? p : LlmProvider.OpenAI;
        var key = LlmOptions.ResolveApiKey(provider, o.ApiKey);
        if (LlmOptions.RequiresApiKey(provider) && string.IsNullOrEmpty(key))
        {
            throw new LlmCompletionException("no_api_key", $"Llm:ApiKey / {LlmOptions.ApiKeyVariable(provider)} is not configured", retryable: false);
        }
        var baseUrl = (string.IsNullOrWhiteSpace(o.BaseUrl) ? LlmOptions.DefaultBaseUrl(provider) : o.BaseUrl.Trim()).TrimEnd('/');
        var requestPayload = CreateBody(provider, request).ToJsonString();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(key))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            using var response = await _http.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var responsePayload = LlmParser.BodyAsJson(body);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                var code = status == HttpStatusCode.TooManyRequests ? "rate_limited" : "provider_error";
                var retryable = status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;
                throw new LlmCompletionException(code, ErrorMessage(body, status), retryable, status) { RequestPayload = requestPayload, ResponsePayload = responsePayload };
            }
            return Parse(body, requestPayload, responsePayload);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LlmCompletionException("provider_timeout", $"no response within {request.Timeout}", retryable: true, inner: ex) { RequestPayload = requestPayload };
        }
        catch (Exception ex) when (ex is not LlmCompletionException and not OperationCanceledException)
        {
            throw new LlmCompletionException("provider_error", $"{baseUrl}: {ex.Message}", retryable: true, inner: ex) { RequestPayload = requestPayload };
        }
    }

    /// <summary>The request body. OpenAI's current models reject max_tokens in favour of max_completion_tokens; Ollama reads max_tokens.</summary>
    internal static JsonObject CreateBody(LlmProvider provider, LlmCompletionRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.Text },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "air_target_facts",
                    ["strict"] = true,
                    ["schema"] = JsonSerializer.SerializeToNode(LlmParser.OutputSchema()),
                },
            },
        };
        body[provider == LlmProvider.Ollama ? "max_tokens" : "max_completion_tokens"] = Math.Clamp(request.MaxOutputTokens, 64, 8192);
        if (provider == LlmProvider.Ollama)
        {
            body["temperature"] = 0; // extraction, not prose: the local model should not improvise
        }
        return body;
    }

    private LlmCompletionResult Parse(string body, string requestPayload, string? responsePayload)
    {
        string? content;
        string? refusal;
        string? id;
        long prompt = 0, cached = 0, completion = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
            var messageElement = root.GetProperty("choices")[0].GetProperty("message");
            content = messageElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            refusal = messageElement.TryGetProperty("refusal", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                prompt = Long(usage, "prompt_tokens");
                completion = Long(usage, "completion_tokens");
                if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    cached = Long(details, "cached_tokens");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmCompletionException("invalid_response", "unexpected chat-completions response: " + ex.Message, retryable: false, inner: ex) { RequestPayload = requestPayload, ResponsePayload = responsePayload };
        }
        // Usage in Anthropic's terms, so the audit and LlmCost read the same for every provider: input excludes cached reads.
        var input = Math.Max(0, prompt - cached);
        if (!string.IsNullOrEmpty(refusal))
        {
            logger.LogInformation("LLM declined to classify the message ({RequestId}): {Refusal}", id, refusal);
            return new LlmCompletionResult(null, true, input, 0, cached, completion, id, requestPayload, responsePayload);
        }
        var json = content ?? "";
        try
        {
            using var _ = JsonDocument.Parse(json); // the caller maps it; here only "is it JSON at all"
        }
        catch (JsonException ex)
        {
            throw new LlmCompletionException("invalid_response", "the model did not return JSON: " + ex.Message, retryable: false, inner: ex) { RequestPayload = requestPayload, ResponsePayload = responsePayload };
        }
        return new LlmCompletionResult(json, false, input, 0, cached, completion, id, requestPayload, responsePayload);
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : 0;

    /// <summary>{"error":{"type":..,"message":..}} (OpenAI), {"error":"..."} (Ollama), else the status and the start of the body.</summary>
    internal static string ErrorMessage(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString()!;
                }
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.GetString() is { } message)
                {
                    var type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    return type is null ? message : $"{type}: {message}";
                }
            }
        }
        catch (JsonException)
        {
        }
        var text = body.Length > 500 ? body[..500] : body;
        return $"HTTP {(int)status}: {text}";
    }

    public void Dispose() => _http.Dispose();
}
