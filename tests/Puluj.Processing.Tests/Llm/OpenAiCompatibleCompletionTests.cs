using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Puluj.Processing.Llm;

namespace Puluj.Processing.Tests.Llm;

public class OpenAiCompatibleCompletionTests
{
    private static readonly LlmCompletionRequest Request = new("Шахед на Полтаву", "system prompt", "gpt-test", "1", 2048, TimeSpan.FromSeconds(5));

    private static (OpenAiCompatibleCompletion Completion, StubHandler Handler) Create(LlmOptions options, HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return (new OpenAiCompatibleCompletion(new StaticMonitor(options), NullLogger<OpenAiCompatibleCompletion>.Instance, handler), handler);
    }

    [Fact]
    public void OpenAI_body_uses_max_completion_tokens_and_a_strict_schema()
    {
        var body = OpenAiCompatibleCompletion.CreateBody(LlmProvider.OpenAI, Request);
        Assert.Equal(2048, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("system prompt", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("Шахед на Полтаву", body["messages"]![1]!["content"]!.GetValue<string>());
        var format = body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.NotNull(format["json_schema"]!["schema"]!["properties"]!["facts"]);
    }

    [Fact]
    public void Ollama_body_uses_max_tokens_and_zero_temperature()
    {
        var body = OpenAiCompatibleCompletion.CreateBody(LlmProvider.Ollama, Request);
        Assert.Equal(2048, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["max_completion_tokens"]);
        Assert.Equal(0, body["temperature"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reads_the_answer_and_usage_in_anthropic_terms()
    {
        const string response = """
            {"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"{\"facts\":[]}","refusal":null},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1200,"completion_tokens":30,"prompt_tokens_details":{"cached_tokens":1000}}}
            """;
        var (completion, handler) = Create(new LlmOptions { Provider = "OpenAI", ApiKey = "sk-test" }, HttpStatusCode.OK, response);
        var result = await completion.CompleteAsync(Request, CancellationToken.None);
        Assert.Equal("""{"facts":[]}""", result.ResponseJson);
        Assert.False(result.Refused);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal(1000, result.CacheReadInputTokens);
        Assert.Equal(0, result.CacheCreationInputTokens);
        Assert.Equal(30, result.OutputTokens);
        Assert.Equal("chatcmpl-1", result.ProviderRequestId);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Uri!.ToString());
        Assert.Equal("Bearer sk-test", handler.Authorization);
        Assert.Equal("gpt-test", JsonNode.Parse(handler.Body!)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ollama_is_called_without_a_key_at_its_base_url()
    {
        const string response = """{"id":"chatcmpl-2","choices":[{"message":{"role":"assistant","content":"{\"facts\":[]}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";
        var (completion, handler) = Create(new LlmOptions { Provider = "ollama", BaseUrl = "http://host.docker.internal:11434/v1/" }, HttpStatusCode.OK, response);
        var result = await completion.CompleteAsync(Request, CancellationToken.None);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal("http://host.docker.internal:11434/v1/chat/completions", handler.Uri!.ToString());
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Reports_a_refusal()
    {
        const string response = """{"id":"x","choices":[{"message":{"role":"assistant","content":null,"refusal":"I can't help with that."}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";
        var (completion, _) = Create(new LlmOptions { Provider = "OpenAI", ApiKey = "sk-test" }, HttpStatusCode.OK, response);
        var result = await completion.CompleteAsync(Request, CancellationToken.None);
        Assert.True(result.Refused);
        Assert.Null(result.ResponseJson);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.InternalServerError, "provider_error", true)]
    [InlineData(HttpStatusCode.Unauthorized, "provider_error", false)]
    public async Task Maps_http_failures_to_the_shared_taxonomy(HttpStatusCode status, string code, bool retryable)
    {
        const string response = """{"error":{"type":"invalid_request_error","message":"Incorrect API key provided"}}""";
        var (completion, _) = Create(new LlmOptions { Provider = "OpenAI", ApiKey = "sk-test" }, status, response);
        var ex = await Assert.ThrowsAsync<LlmCompletionException>(() => completion.CompleteAsync(Request, CancellationToken.None));
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("invalid_request_error: Incorrect API key provided", ex.Message);
        Assert.NotNull(ex.RequestPayload);
        Assert.NotNull(ex.ResponsePayload);
    }

    [Fact]
    public async Task Reads_ollamas_plain_string_error()
    {
        var (completion, _) = Create(new LlmOptions { Provider = "Ollama" }, HttpStatusCode.NotFound, """{"error":"model \"qwen3:8b\" not found, try pulling it first"}""");
        var ex = await Assert.ThrowsAsync<LlmCompletionException>(() => completion.CompleteAsync(Request, CancellationToken.None));
        Assert.Equal("model \"qwen3:8b\" not found, try pulling it first", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Rejects_an_answer_that_is_not_json()
    {
        const string response = """{"id":"x","choices":[{"message":{"role":"assistant","content":"Sure! Here are the facts"}}]}""";
        var (completion, _) = Create(new LlmOptions { Provider = "Ollama" }, HttpStatusCode.OK, response);
        var ex = await Assert.ThrowsAsync<LlmCompletionException>(() => completion.CompleteAsync(Request, CancellationToken.None));
        Assert.Equal("invalid_response", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task OpenAI_without_a_key_fails_before_the_call()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var (completion, handler) = Create(new LlmOptions { Provider = "OpenAI" }, HttpStatusCode.OK, "{}");
            var ex = await Assert.ThrowsAsync<LlmCompletionException>(() => completion.CompleteAsync(Request, CancellationToken.None));
            Assert.Equal("no_api_key", ex.Code);
            Assert.Null(handler.Uri);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Theory]
    [InlineData("Anthropic", true, LlmProvider.Anthropic)]
    [InlineData("openai", true, LlmProvider.OpenAI)]
    [InlineData(" OLLAMA ", true, LlmProvider.Ollama)]
    [InlineData("", true, LlmProvider.Anthropic)]
    [InlineData("gemini", false, default(LlmProvider))]
    [InlineData("7", false, default(LlmProvider))]
    public void Parses_the_provider_setting(string value, bool ok, LlmProvider expected)
    {
        Assert.Equal(ok, new LlmOptions { Provider = value }.TryGetProvider(out var provider));
        if (ok)
        {
            Assert.Equal(expected, provider);
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StaticMonitor(LlmOptions value) : IOptionsMonitor<LlmOptions>
    {
        public LlmOptions CurrentValue => value;
        public LlmOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LlmOptions, string?> listener) => null;
    }
}
