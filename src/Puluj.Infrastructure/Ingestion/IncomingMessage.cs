using System.Text.Json;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>What a collector hands over: the original message, untouched.</summary>
public sealed record IncomingMessage
{
    public required int SourceId { get; init; }
    /// <summary>Legacy id (`{id}`, `{id}:e{edit}`, `{alert}:start`); still written to raw_messages.source_message_id.</summary>
    public required string SourceMessageId { get; init; }
    /// <summary>ADR-0003 identity; when null both are derived from <see cref="SourceMessageId"/> (identity-cases rules).</summary>
    public string? SourceMessageKey { get; init; }
    public string? SourceRevision { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public string? RawText { get; init; }
    public JsonDocument? RawPayload { get; init; }
    public string? Url { get; init; }
}

/// <summary>Outcome of a direct store: <paramref name="RawMessageId"/> is the new row, or the existing one when the identity was already stored (IsNew false).</summary>
public sealed record IngestResult(long? RawMessageId, bool IsNew);
