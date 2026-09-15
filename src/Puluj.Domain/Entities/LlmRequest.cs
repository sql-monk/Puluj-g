namespace Puluj.Domain.Entities;

/// <summary>
/// Immutable audit row for one request sent to the LLM provider. Prices are copied here at request time so reports
/// remain reproducible after a model's public tariff changes.
/// </summary>
public class LlmRequest
{
    public long LlmRequestId { get; set; }
    public long? RawMessageId { get; set; }
    public RawMessage? RawMessage { get; set; }
    public int SourceId { get; set; }
    public Source? Source { get; set; }
    public required DateTimeOffset OccurredAt { get; set; }
    public required string Worker { get; set; }
    public required string Model { get; set; }
    public required string PromptVersion { get; set; }
    public required string Outcome { get; set; }
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    public long? InputTokens { get; set; }
    public long? CacheCreationInputTokens { get; set; }
    public long? CacheReadInputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public int FactsCount { get; set; }
    public required string RequestText { get; set; }
    public required string SystemPrompt { get; set; }
    public string? ResponseText { get; set; }
    public string? Error { get; set; }
}
