using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Processing.Llm;
using Puluj.Processing.Parsing;
using Puluj.Processing.Tests.Support;
using Puluj.Processing.Text;

namespace Puluj.Processing.Tests.Llm;

public class LlmParserTests
{
    private static LlmParser Create()
    {
        var indexes = new StaticIndexes();
        var normalizer = new Normalizer();
        return new LlmParser(new RuleParser(indexes), indexes, normalizer, new StaticMonitor(new LlmOptions { Enabled = false }),
            new LlmBreaker(TimeSpan.FromMinutes(15)), new PulujMetrics(new TestMeterFactory()), TimeProvider.System, NullLogger<LlmParser>.Instance);
    }

    [Fact]
    public void Maps_model_answer_to_facts_using_taxonomy_and_gazetteer()
    {
        var parser = Create();
        var message = new Normalizer().Normalize("Об'єкт летить над Полтавщиною на захід, може шахед.");
        const string json = """
            {"facts":[{"eventType":"TargetObserved","target":{"level":"family","code":"SHAHED"},"hedged":true,"count":null,"countApprox":false,
              "places":[{"name":"Полтавська область","role":"current"},{"name":"Атлантида","role":"destination"}],"directionDeg":270,"launch":false,"segment":0,"quote":null}]}
            """;
        var facts = parser.MapJson(json, message);
        var f = Assert.Single(facts);
        Assert.Equal(IdentificationMethod.Llm, f.Method);
        Assert.Equal("SHAHED", f.Target!.Ref.Code);
        Assert.True(f.Target.Hedged);
        Assert.Equal(ConfidenceLevel.Low, f.Target.EffectiveConfidence);
        Assert.Equal("Полтавська область", Assert.Single(f.Places).Place.Name); // unknown "Атлантида" dropped, never invented
        Assert.Equal(270, f.Direction!.Degrees);
    }

    [Fact]
    public void Ignores_unknown_codes_and_empty_answers()
    {
        var parser = Create();
        var message = new Normalizer().Normalize("test");
        Assert.Empty(parser.MapJson("""{"facts":[{"eventType":"TargetObserved","target":{"level":"model","code":"MADE_UP"},"hedged":false,"count":null,"countApprox":false,"places":[],"directionDeg":null,"launch":false,"segment":0,"quote":null}]}""", message));
        Assert.Empty(parser.MapJson("""{"facts":[]}""", message));
    }

    [Fact]
    public async Task Falls_back_to_rules_when_disabled()
    {
        var parser = Create();
        var message = new Normalizer().Normalize("Шахеди на Сумщині.");
        var facts = await parser.ParseAsync(message, new ParseContext(1, "uk", null), CancellationToken.None);
        Assert.Equal(IdentificationMethod.Rule, Assert.Single(facts).Method);
    }

    private const string UnparsedReport = "Щось летить, будьте уважні."; // a trigger stem, nothing the rules can name

    private static LlmParser CreateWith(LlmOptions options, ILlmCompletion completion, LlmBreaker? breaker = null)
    {
        var indexes = new StaticIndexes();
        return new LlmParser(new RuleParser(indexes), indexes, new Normalizer(), new StaticMonitor(options), breaker ?? new LlmBreaker(TimeSpan.FromMinutes(15)),
            new PulujMetrics(new TestMeterFactory()), TimeProvider.System, NullLogger<LlmParser>.Instance, completion: completion);
    }

    [Fact]
    public async Task Asks_the_configured_provider_with_the_system_prompt_and_timeout()
    {
        const string json = """
            {"facts":[{"eventType":"TargetObserved","target":{"level":"family","code":"SHAHED"},"hedged":false,"count":null,"countApprox":false,
              "places":[{"name":"Полтавська область","role":"current"}],"directionDeg":270,"launch":false,"segment":0,"quote":null}]}
            """;
        var completion = new FakeCompletion(_ => new LlmCompletionResult(json, false, 100, 0, 0, 20, "req-1"));
        var parser = CreateWith(new LlmOptions { Enabled = true, Provider = "Ollama", Ollama = new() { Model = "qwen3:8b" }, TimeoutSeconds = 90 }, completion);
        var facts = await parser.ParseAsync(new Normalizer().Normalize(UnparsedReport), new ParseContext(1, "uk", null), CancellationToken.None);
        Assert.Equal(IdentificationMethod.Llm, Assert.Single(facts).Method);
        var request = Assert.Single(completion.Requests);
        Assert.Equal("qwen3:8b", request.Model);
        Assert.Equal(TimeSpan.FromSeconds(90), request.Timeout);
        Assert.Contains("air-target facts", request.SystemPrompt);
    }

    [Fact]
    public async Task A_rejected_request_pauses_the_model_whatever_the_provider()
    {
        var breaker = new LlmBreaker(TimeSpan.FromMinutes(15));
        var completion = new FakeCompletion(_ => throw new LlmCompletionException("provider_error", "invalid_api_key", retryable: false, HttpStatusCode.Unauthorized));
        var parser = CreateWith(new LlmOptions { Enabled = true, Provider = "OpenAI", OpenAI = new() { Model = "gpt-test", ApiKey = "sk-bad" } }, completion, breaker);
        var facts = await parser.ParseAsync(new Normalizer().Normalize(UnparsedReport), new ParseContext(1, "uk", null), CancellationToken.None);
        Assert.Empty(facts);
        Assert.True(breaker.IsOpen(DateTimeOffset.UtcNow, out var reason));
        Assert.Equal("invalid_api_key", reason);
    }

    [Fact]
    public async Task An_unreachable_server_is_not_a_pause()
    {
        var breaker = new LlmBreaker(TimeSpan.FromMinutes(15));
        var completion = new FakeCompletion(_ => throw new LlmCompletionException("provider_error", "connection refused", retryable: true));
        var parser = CreateWith(new LlmOptions { Enabled = true, Provider = "Ollama" }, completion, breaker);
        Assert.Empty(await parser.ParseAsync(new Normalizer().Normalize(UnparsedReport), new ParseContext(1, "uk", null), CancellationToken.None));
        Assert.False(breaker.IsOpen(DateTimeOffset.UtcNow, out _));
        Assert.Equal(1, breaker.Failures);
    }

    [Theory]
    [InlineData("Gemini", null)]
    [InlineData("Anthropic", null)] // no key (the variable is not set in tests)
    public async Task A_misconfigured_provider_leaves_the_rules_alone(string provider, string? key)
    {
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is not null)
        {
            return;
        }
        var completion = new FakeCompletion(_ => throw new InvalidOperationException("must not be called"));
        var parser = CreateWith(new LlmOptions { Enabled = true, Provider = provider, Anthropic = new() { Model = "claude-opus-5", ApiKey = key } }, completion);
        Assert.Empty(await parser.ParseAsync(new Normalizer().Normalize(UnparsedReport), new ParseContext(1, "uk", null), CancellationToken.None));
        Assert.Empty(completion.Requests);
    }

    private sealed class FakeCompletion(Func<LlmCompletionRequest, LlmCompletionResult> answer) : ILlmCompletion
    {
        public List<LlmCompletionRequest> Requests { get; } = [];

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class StaticMonitor(LlmOptions value) : IOptionsMonitor<LlmOptions>
    {
        public LlmOptions CurrentValue => value;
        public LlmOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LlmOptions, string?> listener) => null;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
