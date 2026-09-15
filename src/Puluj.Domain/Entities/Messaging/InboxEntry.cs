namespace Puluj.Domain.Entities.Messaging;

/// <summary>
/// Consumer-side deduplication (ADR-0004 §3, ADR-0006 `messaging.inbox`): one row per (subscription, event), inserted in
/// the same transaction as the business effect and the delivery receipt. A redelivery that finds the row is ACKed without
/// repeating the effect; a row with outcome `quarantined` is nacked to the DLQ again (idempotent transfer, W6a-2).
/// </summary>
public class InboxEntry
{
    public required string SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Terminal outcome of the delivery: completed | noop | quarantined.</summary>
    public required string Outcome { get; set; }
}
