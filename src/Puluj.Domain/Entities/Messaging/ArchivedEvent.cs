using System.Text.Json;

namespace Puluj.Domain.Entities.Messaging;

/// <summary>
/// Append-only archive of every published envelope (ADR-0006 `messaging.events`), written by the `archive` subscription
/// in its own inbox transaction. It is the source for replay and for the backfill of a subscription activated later;
/// it does not depend on the outbox, which may be cleaned up once this row exists (plan §15.2).
/// </summary>
public class ArchivedEvent
{
    public Guid EventId { get; set; }
    public required string EventType { get; set; }
    public required string Lane { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public long? RawMessageId { get; set; }
    public Guid ProcessingRunId { get; set; }
    public int TopologyVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public required JsonDocument Envelope { get; set; }
    public DateTimeOffset ArchivedAt { get; set; }
}

/// <summary>Causal link for batch results with several input events (ADR-0006 `messaging.event_links`).</summary>
public class EventLink
{
    public Guid OutputEventId { get; set; }
    public Guid InputEventId { get; set; }
}
