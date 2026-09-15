using System.Text.Json;
using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>Spec §5. Immutable original message. Only the processing-status columns may change after insert.</summary>
public class RawMessage
{
    public long RawMessageId { get; set; }
    public int SourceId { get; set; }
    public Source? Source { get; set; }
    /// <summary>Legacy identifier inside the source (telegram message id, `{id}:e{edit}` for an edit, alert `{id}:start`). Unique per source; kept for compatibility, the identity is <see cref="SourceMessageKey"/> + <see cref="SourceRevision"/>.</summary>
    public required string SourceMessageId { get; set; }
    /// <summary>Stable key of the post inside the source without the revision (ADR-0003): telegram message id, alerts `{id}:start|end`.</summary>
    public required string SourceMessageKey { get; set; }
    /// <summary>Revision of the same key: `0` for the original, `e{editUnix}` for a Telegram edit. Unique (source, key, revision) is the raw identity (P04).</summary>
    public required string SourceRevision { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? RawText { get; set; }
    public JsonDocument? RawPayload { get; set; }
    public string? Url { get; set; }
    /// <summary>SHA-256 of source code + text/payload: a similarity index (same content under a new source id is a new post, plan §5.2), no longer unique since P04.</summary>
    public required string Hash { get; set; }

    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Pending;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    /// <summary>Processor instance that took the message (kept after processing as provenance); null while Pending.</summary>
    public string? ClaimedBy { get; set; }
    /// <summary>When it was taken; an InProgress claim older than the lease is returned to Pending by any instance.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
    /// <summary>Wall time of a successful processing (parse, lock wait, store) in ms; null for Skipped and Failed.</summary>
    public int? ProcessingMs { get; set; }

    public ICollection<Target> Targets { get; set; } = [];
}
