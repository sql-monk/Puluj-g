namespace Puluj.Contracts;

/// <summary>Aggregated audit report shown on the admin LLM page. Monetary values are USD estimates from the tariff
/// copied into each request, not an invoice from the provider.</summary>
public sealed record LlmUsageReportDto(
    DateTimeOffset From, DateTimeOffset To,
    long Calls, long WithFacts, long Empty, long Refusals, long Failures,
    long InputTokens, long CacheWriteTokens, long CacheReadTokens, long OutputTokens,
    decimal EstimatedCostUsd, double? MeanDurationMs,
    IReadOnlyList<LlmUsageBucketDto> Timeline,
    IReadOnlyList<LlmRequestDto> Recent);

public sealed record LlmUsageBucketDto(DateTimeOffset At, long Calls, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);

/// <summary>Compact audit row; the body text is intentionally loaded only after the operator opens its details.</summary>
public sealed record LlmRequestDto(
    long Id, DateTimeOffset OccurredAt, long? RawMessageId, int SourceId, string SourceCode, string Worker,
    string Model, string PromptVersion, string Outcome, int? StatusCode, int DurationMs,
    long? InputTokens, long? CacheWriteTokens, long? CacheReadTokens, long? OutputTokens,
    decimal? EstimatedCostUsd, int FactsCount, string? Error);

/// <summary>One page of the LLM request history, newest first.</summary>
/// <param name="Total">Requests in the period that match the filter (not only this page).</param>
/// <param name="NextBeforeId">Pass as `beforeId` for the next (older) page; null on the last page.</param>
public sealed record LlmRequestPageDto(IReadOnlyList<LlmRequestDto> Requests, long Total, long? NextBeforeId);

public sealed record LlmRequestDetailDto(LlmRequestDto Request, string RequestText, string SystemPrompt, string? ResponseText);
