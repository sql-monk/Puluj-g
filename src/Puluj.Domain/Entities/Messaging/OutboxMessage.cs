using System.Text.Json;

namespace Puluj.Domain.Entities.Messaging;

/// <summary>
/// Transactional outbox row (ADR-0004 §2, ADR-0006 `messaging.outbox`): an event committed together with the business
/// result it describes, waiting for the relay to publish it and the broker to confirm. <see cref="EventId"/> is generated
/// once here and never changes on republish; a row stays until <see cref="ConfirmedAt"/> is set, so a crash anywhere
/// between commit and confirm only means one more publish with the same id (the consumers' inbox absorbs the duplicate).
/// <see cref="TargetQueue"/> is set only for admin redelivery of a quarantined event straight into one subscription's queue
/// (ADR-0004 W6b): such a row bypasses the fan-out exchange and may coexist with the original row of the same event.
/// </summary>
public class OutboxMessage
{
    public long OutboxId { get; set; }
    public Guid EventId { get; set; }
    public required string EventType { get; set; }
    public required string Lane { get; set; }
    public required string RoutingKey { get; set; }
    public string? TargetQueue { get; set; }
    /// <summary>Whether the event type is a replay source (ADR-0002): cleanup then waits for the archive receipt.</summary>
    public bool ReplaySource { get; set; }
    public required JsonDocument Envelope { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    /// <summary>Publish attempts so far (transport retries; the business `published_at` inside the envelope never changes).</summary>
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}
