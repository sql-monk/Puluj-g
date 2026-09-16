using System.Text.Json;

namespace Puluj.Domain.Entities.Messaging;

/// <summary>
/// P13 (plan §9.2, ADR-0012): the runtime control of one subscription lane — the precise scope an operator pauses. Consumers
/// read it on a short poll: `paused` cancels the lane's consumer tag (in-flight deliveries finish, ACK after commit),
/// `draining` keeps consuming until the queue is empty and then becomes `paused` by itself, `active` consumes.
/// The registry status of the subscription (`messaging.subscriptions.status`) stays the planned/expected-set switch.
/// </summary>
public class SubscriptionLane
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Draining = "draining";

    public required string SubscriptionId { get; set; }
    public required string Lane { get; set; }
    public required string State { get; set; }
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>Every operator control (pause/resume/drain/drained/retry/waive/scale) with actor and reason; details carry the scope and outcome.</summary>
public class ControlAudit
{
    public long AuditId { get; set; }
    public required string Action { get; set; }
    public string? SubscriptionId { get; set; }
    public string? Lane { get; set; }
    public required string Actor { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset At { get; set; }
    public JsonDocument? Details { get; set; }
}
