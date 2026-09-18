using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>Air-raid alert interval for an administrative unit (from structured sources like alerts.in.ua).</summary>
public class AirAlert
{
    public long AirAlertId { get; set; }
    public int PlaceId { get; set; }
    public Place? Place { get; set; }
    public AirAlertType AlertType { get; set; }
    /// <summary>Yellow / red for administrations that publish levels; Unknown for plain on/off alerts.</summary>
    public AirAlertLevel Level { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int SourceId { get; set; }
    public long? StartRawMessageId { get; set; }
    public long? EndRawMessageId { get; set; }
    /// <summary>Identifier of the alert in the source, for idempotent updates.</summary>
    public required string SourceAlertId { get; set; }
}
