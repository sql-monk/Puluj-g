namespace Puluj.Processing.Llm;

/// <summary>Pure estimate from Anthropic's returned usage counters. The caller persists its current price card with the
/// audit row, so this calculation never needs to reinterpret old requests under a newer tariff.</summary>
public static class LlmCost
{
    public static decimal Calculate(LlmOptions options, long inputTokens, long cacheWriteTokens, long cacheReadTokens, long outputTokens) =>
        Math.Round(
            (inputTokens * options.InputUsdPerMillionTokens + cacheWriteTokens * options.CacheWriteUsdPerMillionTokens + cacheReadTokens * options.CacheReadUsdPerMillionTokens + outputTokens * options.OutputUsdPerMillionTokens) / 1_000_000m,
            9, MidpointRounding.AwayFromZero);
}
