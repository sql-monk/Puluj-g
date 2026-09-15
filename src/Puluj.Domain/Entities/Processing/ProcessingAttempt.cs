using System.Text.Json;

namespace Puluj.Domain.Entities.Processing;

/// <summary>
/// One attempt of a consumer to process one delivery (ADR-0004 §5, ADR-0006 `processing.attempts`). Business retries and
/// their limit live here, not in the broker's delivery count (ADR-0001 §8): the row is written before the work starts,
/// so a crash leaves an `interrupted` attempt that the next delivery can see and count.
/// </summary>
public class ProcessingAttempt
{
    public long AttemptId { get; set; }
    public required string SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    /// <summary>`{subscription_id}:{event_id}` for message deliveries; long jobs (LLM, P06) use their own key.</summary>
    public required string JobKey { get; set; }
    public long? StageResultId { get; set; }
    public required string Worker { get; set; }
    /// <summary>running | succeeded | failed | interrupted | superseded</summary>
    public required string State { get; set; }
    public int FencingToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? Error { get; set; }
    public string? RetryReason { get; set; }
    public long? RetryOfAttemptId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// Expected and terminal delivery receipts (ADR-0002, ADR-0006 `processing.deliveries`). The expected rows are written
/// when the event is committed to the outbox, so the set of interested subscriptions is fixed by the topology version
/// of the moment of publication, not by which processes happen to run later. A consumer writes its terminal
/// <see cref="Outcome"/> in the transaction of its result; reconciliation looks for expected rows without one.
/// </summary>
public class Delivery
{
    public Guid EventId { get; set; }
    public required string SubscriptionId { get; set; }
    public int TopologyVersion { get; set; }
    public DateTimeOffset ExpectedAt { get; set; }
    /// <summary>completed | noop | quarantined | waived; null while expected.</summary>
    public string? Outcome { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public long? AttemptId { get; set; }
}

/// <summary>
/// Durable quarantine (ADR-0004 §6.3, W6): the delivery, its envelope and the error, kept until an operator retries or
/// waives it. This table is the source of truth; the broker DLQ is the operational copy. A retry republishes the
/// envelope stored here — the outbox row may have been cleaned up and the archive has no row when the archive
/// subscription itself quarantined the event.
/// </summary>
public class QuarantineEntry
{
    public long QuarantineId { get; set; }
    public required string SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public required string Lane { get; set; }
    /// <summary>attempts_exhausted | invalid_payload | incompatible_schema | unknown_event | delivery_limit</summary>
    public required string Reason { get; set; }
    public string? Error { get; set; }
    public required JsonDocument Envelope { get; set; }
    public JsonDocument? Headers { get; set; }
    public long? LastAttemptId { get; set; }
    public DateTimeOffset QuarantinedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    /// <summary>retried | waived</summary>
    public string? Resolution { get; set; }
    public long? RetryOutboxId { get; set; }
}
