using Microsoft.Extensions.Configuration;
using Puluj.Processing.Llm;

namespace Puluj.Processing.Tests.Llm;

public class LlmOptionsTests
{
    private static LlmOptions Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(LlmOptions.Section).Get<LlmOptions>()!;

    private static readonly Dictionary<string, string?> AllProviders = new()
    {
        ["Llm:Anthropic:ApiKey"] = "sk-ant",
        ["Llm:OpenAI:ApiKey"] = "sk-openai",
        ["Llm:OpenAI:Model"] = "gpt-test",
        ["Llm:OpenAI:InputUsdPerMillionTokens"] = "1.5",
        ["Llm:Ollama:BaseUrl"] = "http://ollama:11434/v1",
    };

    [Theory]
    [InlineData("Anthropic", "sk-ant", "claude-opus-5", "claude-opus-5")]
    [InlineData("openai", "sk-openai", "gpt-test", "openai/gpt-test")]
    [InlineData("Ollama", null, "qwen3:8b", "ollama/qwen3:8b")]
    public void Every_provider_keeps_its_own_key_and_the_switch_picks_one(string provider, string? key, string model, string audited)
    {
        var options = Bind(new(AllProviders) { ["Llm:Provider"] = provider });
        Assert.True(options.TryGetProvider(out var selected));
        Assert.Equal(key, options.For(selected).ApiKey);
        Assert.Equal(model, options.Current.Model);
        Assert.Equal(audited, options.AuditModel);
    }

    [Fact]
    public void Configured_values_keep_the_other_defaults_of_the_section()
    {
        var options = Bind(new(AllProviders));
        Assert.Equal(5m, options.Anthropic.InputUsdPerMillionTokens); // defaults survive a bound key in the same section
        Assert.Equal(1.5m, options.OpenAI.InputUsdPerMillionTokens);
        Assert.Equal("qwen3:8b", options.Ollama.Model);
        Assert.Equal("http://ollama:11434/v1", options.Ollama.BaseUrl);
    }

    [Fact]
    public void An_unknown_provider_still_binds()
    {
        var options = Bind(new(AllProviders) { ["Llm:Provider"] = "Gemini" });
        Assert.False(options.TryGetProvider(out _));
        Assert.Equal("claude-opus-5", options.Current.Model);
    }
}
