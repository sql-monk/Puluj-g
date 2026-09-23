using Microsoft.Extensions.Options;

namespace Puluj.Processing.Llm;

/// <summary>
/// The <see cref="ILlmCompletion"/> the pipeline uses: picks the provider from Llm:Provider on every call, so switching
/// between Anthropic, OpenAI and Ollama in the admin UI takes effect without a restart.
/// </summary>
public sealed class LlmCompletionRouter(IOptionsMonitor<LlmOptions> options, AnthropicCompletion anthropic, OpenAiCompatibleCompletion openAi) : ILlmCompletion
{
    public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        if (!options.CurrentValue.TryGetProvider(out var provider))
        {
            throw new LlmCompletionException("unknown_provider", $"Llm:Provider '{options.CurrentValue.Provider}' is not one of Anthropic, OpenAI, Ollama", retryable: false);
        }
        return provider == LlmProvider.Anthropic ? anthropic.CompleteAsync(request, ct) : openAi.CompleteAsync(request, ct);
    }
}
