namespace Puluj.Contracts;

// ---- P13 (ADR-0012): message-platform operations. The snapshot is computed by Puluj.Infrastructure (Messaging/Ops) from
// the receipts (`processing.deliveries`/`attempts`/`quarantine`), the outbox/inbox tables and the worker status documents;
// the broker's management API is an optional second source and is marked as such per number.

/// <summary>One lane of one consumer replica, as the worker reports it in <see cref="WorkerStatusDto.Consumers"/>.</summary>
public sealed record ConsumerLaneDto(string Subscription, string Lane, string Queue, string State, bool Consuming, int InFlight, int Prefetch, string ConsumerTag, long Delivered, long Duplicates, long Requeued);

/// <summary>The process's broker connection as it sees it (false before first use and after a loss until re-opened).</summary>
public sealed record BrokerStatusDto(bool Connected, string Endpoint);

/// <summary>Last reconciliation pass of the `messaging` worker (`Runtime:Reconciliation:Report`).</summary>
public sealed record ReconciliationReportDto(
    DateTimeOffset At,
    string Worker,
    long OutboxUnconfirmed,
    double OutboxOldestAgeSeconds,
    long OverdueCount,
    IReadOnlyList<string> UnknownSubscriptions,
    long QuarantineOpen,
    IReadOnlyList<string> DeclareFailed,
    int OutboxDeleted,
    int InboxDeleted);

/// <summary>Operator state of one lane (`messaging.subscription_lanes`); absent row = active.</summary>
public sealed record LaneStateDto(string State, string? Reason, string? Actor, DateTimeOffset? ChangedAt);

/// <summary>Counters of one subscription × lane. Percentiles are over the last hour; nulls mean "no sample".</summary>
public sealed record SubscriptionLaneOpsDto(
    string Subscription,
    string Lane,
    bool Required,
    /// <summary>Registry status of the subscription (active | paused | retired | planned).</summary>
    string RegistryStatus,
    LaneStateDto LaneState,
    long Pending,
    long InFlight,
    long RetryHour,
    long AdminRetryHour,
    long Quarantined,
    double? OldestPendingAgeSeconds,
    double? EventTimeLagSeconds,
    double? OldestRunningAttemptAgeSeconds,
    double? WaitP50Ms, double? WaitP95Ms, double? WaitP99Ms,
    double? ProcessingP50Ms, double? ProcessingP95Ms, double? ProcessingP99Ms,
    long ExpectedHour, long CompletedHour, long NoopHour, long FailedHour,
    long Expected5m, long Completed5m,
    /// <summary>Live worker replicas consuming this lane (from their status documents).</summary>
    int Consumers,
    /// <summary>Broker view when the management API is configured; null otherwise.</summary>
    long? Ready, long? Unacked, int? BrokerConsumers,
    /// <summary>db | management — where Ready/Unacked come from.</summary>
    string Source);

/// <summary>Root messages, not stage jobs (§9.1): a root is complete when every registered expected branch is terminal.</summary>
public sealed record RootsOpsDto(long ReceivedHour, long CompletedHour, long Pending, long NeedsAttention);

public sealed record BrokerNodeDto(string Name, bool Running, bool MemAlarm, bool DiskAlarm);

public sealed record BrokerManagementDto(bool Available, string? Reason, IReadOnlyList<BrokerNodeDto> Nodes);

/// <summary>Connected = any live worker with a broker role reports an open connection; null when no such worker reports.</summary>
public sealed record BrokerOpsDto(bool? Connected, IReadOnlyList<string> ConnectedWorkers, IReadOnlyList<string> DisconnectedWorkers, BrokerManagementDto? Management);

public sealed record OutboxOpsDto(long Unconfirmed, double? OldestAgeSeconds, long RelayRetries, long Unroutable, double? ConfirmP50Ms, double? ConfirmP95Ms, long PublishedHour);

public sealed record InboxOpsDto(long RowsHour, long SupersededHour, long Processing);

public sealed record WorkerOpsDto(
    string Name,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? StatusAt,
    bool Stale,
    /// <summary>Stale heartbeat while attempts of this worker are still `running`.</summary>
    bool Stuck,
    long RunningAttempts,
    long CompletedHour,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    IReadOnlyList<string> Roles,
    BrokerStatusDto? Broker,
    LlmStatusDto? Llm,
    IReadOnlyList<ConsumerLaneDto> Consumers);

public sealed record BackfillOpsDto(string Source, string? Status, string? Checkpoint, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures, string? LastError);

/// <summary>info | warn | error; scope = `subscription/lane`, `worker:name`, `outbox`, `broker`, …</summary>
public sealed record AlarmDto(string Code, string Severity, string Scope, string Message);

public sealed record MessagingOpsDto(
    DateTimeOffset At,
    int TopologyVersion,
    IReadOnlyList<SubscriptionLaneOpsDto> Subscriptions,
    RootsOpsDto Roots,
    BrokerOpsDto Broker,
    OutboxOpsDto Outbox,
    InboxOpsDto Inbox,
    ReconciliationReportDto? Reconciliation,
    IReadOnlyList<WorkerOpsDto> Workers,
    IReadOnlyList<BackfillOpsDto> Backfill,
    IReadOnlyList<AlarmDto> Alarms,
    OpsSloDto Slo);

/// <summary>The thresholds the alarms were evaluated with (`Ops:Slo`), echoed so the UI can say why.</summary>
public sealed record OpsSloDto(
    IReadOnlyDictionary<string, int> OldestAgeSeconds,
    int OutboxUnconfirmedSeconds,
    int OutboxCriticalSeconds,
    int StaleHeartbeatSeconds,
    int InflightStuckSeconds,
    int RequiredConsumerMissingSeconds);

// The single-operator admin UI omits audit metadata. API clients may still supply it when they have a real identity
// or a useful reason; the messaging endpoints otherwise record their explicit local-admin defaults.
public sealed record LaneControlRequest(string State, string? Actor = null, string? Reason = null);
public sealed record QuarantineActionRequest(string? Actor = null, string? Reason = null);
public sealed record MessagingScaleRequest(string Service, int Replicas, string? Actor = null, string? Reason = null);

public sealed record QuarantineRowDto(long QuarantineId, string SubscriptionId, Guid EventId, string Lane, string Reason, string? Error, DateTimeOffset QuarantinedAt, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? Resolution, string? EventType, long? RawMessageId);

public sealed record ControlAuditDto(long AuditId, string Action, string? SubscriptionId, string? Lane, string Actor, string Reason, DateTimeOffset At, System.Text.Json.JsonElement? Details);

// ---- Message explorer (§8.7): one card with the whole lifecycle of a raw message.

/// <summary>
/// One row in the operator's message list.  Counts are durable evidence, not guesses: a target is a legacy target
/// projection, an observation is a catalog event fact, and an LLM call is an immutable provider audit row.
/// </summary>
public sealed record MessageReactionDto(string Kind, string Value, int Count);

public sealed record MessageSearchRowDto(long RawMessageId, int SourceId, string SourceCode, string SourceMessageId, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt, string Status,
    int Extractions, int Observations, int Targets, int LlmCalls, string? AnalysisOutcome, string? Method, string? LastOutcome, string TextPreview,
    IReadOnlyList<MessageReactionDto> Reactions);

/// <summary>A bounded, stable page of raw-message revisions for the operator explorer.</summary>
public sealed record MessageSearchPageDto(IReadOnlyList<MessageSearchRowDto> Items, long TotalCount, int Page, int PageSize);

public sealed record LifecycleAttemptDto(long AttemptId, string Worker, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error, long? RetryOfAttemptId, string? RetryReason);

public sealed record LifecycleDeliveryDto(string Subscription, string? Lane, DateTimeOffset ExpectedAt, string? Outcome, DateTimeOffset? CompletedAt, string? Reason, string? Actor, IReadOnlyList<LifecycleAttemptDto> Attempts, bool AttemptsTruncated);

public sealed record LifecycleEventDto(Guid EventId, string EventType, string Lane, DateTimeOffset OccurredAt, DateTimeOffset? PublishedAt, DateTimeOffset? ConfirmedAt, Guid? CausationId, string Producer, IReadOnlyList<LifecycleDeliveryDto> Deliveries);

public sealed record LifecycleExtractionDto(Guid ExtractionId, Guid RunId, int Version, string Method, string Outcome, string? Versions, string FinalizedBy, DateTimeOffset CreatedAt, int Observations, string? Error);

public sealed record LifecycleObservationDto(Guid ObservationId, Guid ExtractionId, string Kind, string Category, DateTimeOffset EffectiveAt, string PayloadPreview, long? LegacyTargetId);

/// <summary>The direct legacy projection of this raw message.  It is listed even when it has not joined a track yet.</summary>
public sealed record LifecycleTargetDto(long TargetId, int SegmentIndex, string EventType, string? EventKind, DateTimeOffset ObservedAt, int? ObjectCount, string? Classification, string? Location, double? LocationAccuracyKm, long? DuplicateOfTargetId);

/// <summary>Compact, per-message LLM audit.  Prompt and response bodies remain behind the existing on-demand audit endpoint.</summary>
public sealed record LifecycleLlmRequestDto(long LlmRequestId, DateTimeOffset OccurredAt, string Model, string PromptVersion, string Outcome, int? StatusCode, int DurationMs,
    long? InputTokens, long? CacheWriteTokens, long? CacheReadTokens, long? OutputTokens, decimal? EstimatedCostUsd, int FactsCount, string? Error);

public sealed record LifecycleQuarantineDto(long QuarantineId, string SubscriptionId, string Lane, string Reason, string? Error, DateTimeOffset QuarantinedAt, DateTimeOffset? ResolvedAt, string? Resolution, string EnvelopePreview, bool EnvelopeTruncated);

public sealed record LifecycleRefDto(string Kind, long Id, string Label);

/// <summary>waiting | completed | failed lists over the expected branches of the root (`subscription/lane`).</summary>
public sealed record LifecycleSummaryDto(string Completion, IReadOnlyList<string> Waiting, IReadOnlyList<string> Completed, IReadOnlyList<string> Failed);

public sealed record MessageLifecycleDto(
    long RawMessageId, int SourceId, string SourceCode, string SourceMessageId, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt, string Status, string? Text, string? Url,
    IReadOnlyList<LifecycleEventDto> Events, bool EventsTruncated,
    IReadOnlyList<LifecycleExtractionDto> Extractions,
    IReadOnlyList<LifecycleObservationDto> Observations,
    IReadOnlyList<LifecycleTargetDto> Targets,
    IReadOnlyList<LifecycleLlmRequestDto> LlmRequests,
    IReadOnlyList<LifecycleRefDto> Derived,
    IReadOnlyList<LifecycleQuarantineDto> Quarantine,
    LifecycleSummaryDto Summary);

// ---- P14 (ADR-0005): runs and generations.

public sealed record RunCheckpointDto(long Published, long Total, long LastRawMessageId, DateTimeOffset? LastPublishedAt, bool Done, string? Error);

public sealed record RunDto(
    Guid RunId, string Lane, string Kind,
    /// <summary>created | running | paused | verified | promoted | rolled_back | cancelled | failed | completed | superseded</summary>
    string State,
    Guid? GenerationId, bool GenerationActive, DateTimeOffset? PromotedAt, DateTimeOffset? RolledBackAt, string? VerifiedBy,
    Guid? SupersedesRunId, Guid? ReplaysRunId,
    System.Text.Json.JsonElement? Versions, System.Text.Json.JsonElement? Scope, RunCheckpointDto? Checkpoint,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? FinishedAt);

public sealed record ReplayCreateRequest(int[]? SourceIds, DateTimeOffset From, DateTimeOffset To, string Actor, string Reason);

public sealed record RunActionRequest(string Actor, string Reason, DateTimeOffset? Watermark = null);
