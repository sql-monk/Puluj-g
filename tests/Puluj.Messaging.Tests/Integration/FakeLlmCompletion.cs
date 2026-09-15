using System.Collections.Concurrent;
using Puluj.Processing.Llm;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// Scripted provider for the llm-worker tests: no key, no network, the same failure taxonomy as the real one
/// (<see cref="LlmCompletionException"/>). Answers are consumed in order; when the script is empty the default answer
/// (one UAV target at the first place the text names) is returned. Every request is recorded.
/// </summary>
public sealed class FakeLlmCompletion : ILlmCompletion
{
    public const string DefaultAnswer = """{"facts":[{"eventType":"TargetObserved","target":{"level":"category","code":"UAV"},"hedged":false,"count":null,"countApprox":false,"places":[{"name":"Суми","role":"current"}],"directionDeg":null,"launch":false,"segment":0,"quote":"щось летить на Суми"}]}""";

    private readonly ConcurrentQueue<Func<LlmCompletionRequest, Task<LlmCompletionResult>>> _script = new();
    public ConcurrentBag<LlmCompletionRequest> Calls { get; } = [];
    /// <summary>Optional gate: the call blocks here until released (simulates a slow provider so a lease can expire).</summary>
    public SemaphoreSlim? Gate { get; set; }

    public FakeLlmCompletion Answer(string json, string? providerRequestId = null) =>
        Enqueue(_ => Task.FromResult(new LlmCompletionResult(json, false, 812, 0, 0, 96, providerRequestId ?? $"msg_{Guid.NewGuid():N}")));

    public FakeLlmCompletion Refuse() => Enqueue(_ => Task.FromResult(new LlmCompletionResult(null, true, 800, 0, 0, 1, "msg_refused")));

    public FakeLlmCompletion Fail(string code, bool retryable, System.Net.HttpStatusCode? status = null) =>
        Enqueue(_ => throw new LlmCompletionException(code, $"scripted {code}", retryable, status));

    public FakeLlmCompletion Enqueue(Func<LlmCompletionRequest, Task<LlmCompletionResult>> answer)
    {
        _script.Enqueue(answer);
        return this;
    }

    public void Reset()
    {
        while (_script.TryDequeue(out _))
        {
        }
        Calls.Clear();
        Gate = null;
    }

    public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (Gate is { } gate)
        {
            await gate.WaitAsync(ct);
        }
        if (_script.TryDequeue(out var scripted))
        {
            return await scripted(request);
        }
        return new LlmCompletionResult(DefaultAnswer, false, 812, 0, 0, 96, $"msg_{Guid.NewGuid():N}");
    }
}
