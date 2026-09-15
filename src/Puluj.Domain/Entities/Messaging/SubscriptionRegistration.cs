using System.Text.Json;

namespace Puluj.Domain.Entities.Messaging;

/// <summary>
/// Versioned registry of subscriptions (ADR-0002, ADR-0006 `messaging.subscriptions`): a copy of `topology.json` per
/// topology version, taken when a build with that version first touches the database. Bindings, lanes and the required
/// flag come from the file; <see cref="Status"/> starts from the file and is then owned by the database (pause/waiver
/// are operator actions with audit). Only `active`/`paused` subscriptions are expected to complete a delivery and have
/// their queues declared; `planned` ones get nothing until they are activated in a new topology version.
/// </summary>
public class SubscriptionRegistration
{
    public required string SubscriptionId { get; set; }
    public int TopologyVersion { get; set; }
    public bool Required { get; set; }
    /// <summary>planned | active | paused | retired</summary>
    public required string Status { get; set; }
    public required JsonDocument Bindings { get; set; }
    public required JsonDocument Lanes { get; set; }
    public required string QueuePolicy { get; set; }
    public string? OwnerTask { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    /// <summary>Reason and actor of the last pause/waiver, for audit.</summary>
    public JsonDocument? Waiver { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which `topology.json` versions this database has seen (ADR-0006 `messaging.topology_versions`).</summary>
public class TopologyVersionRecord
{
    public int TopologyVersion { get; set; }
    /// <summary>SHA-256 of the embedded topology.json: a different file with the same version number is refused.</summary>
    public required string Hash { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public required string AppliedBy { get; set; }
}
