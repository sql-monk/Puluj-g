using System;

namespace Puluj.Domain.Entities.Analytics;

/// <summary>
/// P15 (ADR-0013): the lifecycle of one raw message in one processing run — the projection behind the «Аналітика повідомлень»
/// page. Root fields describe the post (counted once per `RawMessageId` across runs); analysis/domain/cost fields come from the
/// `message-analytics` subscription events, or from the backfill over durable evidence (`SourceOfTruth = backfill`). Timings of
/// messages processed before the stage tables existed are unknown: `TimingsAvailable = false`, never invented.
/// </summary>
public class MessageLifecycle
{
    public const string SourceEvent = "event", SourceBackfill = "backfill", SourceReconciliation = "reconciliation";
    public const string OutcomeLegacy = "legacy";

    public long RawMessageId { get; set; }
    public Guid RunId { get; set; }
    public int SourceId { get; set; }
    /// <summary>Post identity (ADR-0003): a post = `(source_id, source_message_key)`, an edit = another revision of the same post. Root denominators: raw rows, posts, edits.</summary>
    public required string SourceMessageKey { get; set; }
    public required string SourceRevision { get; set; }
    /// <summary>live | history | replay | legacy</summary>
    public required string Lane { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? StoredAt { get; set; }
    public bool HasText { get; set; }
    public int TextLength { get; set; }
    public bool HasPayload { get; set; }
    public bool IsEdit { get; set; }
    public DateTimeOffset? AnalyzedAt { get; set; }
    /// <summary>completed | no_facts | unsupported | needs_review | failed | legacy (pre-stage pipeline, from processing_status)</summary>
    public string? AnalysisOutcome { get; set; }
    /// <summary>rules | llm | legacy</summary>
    public string? Method { get; set; }
    public int FactCount { get; set; }
    public int UnlocatedFacts { get; set; }
    public string? Versions { get; set; }
    public string? Timings { get; set; }
    public bool TimingsAvailable { get; set; }
    public string? Error { get; set; }
    public string[] ExpectedBranches { get; set; } = [];
    /// <summary>Branches that reported an aggregate change (`track-worker`, `alert-worker`, `incident-worker`); a `noop` branch is filled by the reconciliation.</summary>
    public string[] BranchesDone { get; set; } = [];
    public DateTimeOffset? DomainCompletedAt { get; set; }
    /// <summary>False when the receipts of this raw are gone (retention) or never existed (pre-P03): completion is unknown, not «not completed».</summary>
    public bool CompletionAvailable { get; set; } = true;
    /// <summary>Generation of the last incident change (visibility is computed against the active generation at query time).</summary>
    public Guid? GenerationId { get; set; }
    public long[] IncidentIds { get; set; } = [];
    public long[] TrackIds { get; set; } = [];
    public long[] AlertIds { get; set; } = [];
    public int LlmCalls { get; set; }
    public long LlmInputTokens { get; set; }
    public long LlmCacheTokens { get; set; }
    public long LlmOutputTokens { get; set; }
    public decimal LlmCostUsd { get; set; }
    public int LlmLatencyMs { get; set; }
    /// <summary>event | backfill | reconciliation — which writer produced the analysis fields; the backfill never overwrites event-sourced fields.</summary>
    public required string SourceOfTruth { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
