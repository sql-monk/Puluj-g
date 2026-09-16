using System.Text.Json.Serialization;

namespace Puluj.Infrastructure.Messaging;

/// <summary>Payload of the `puluj_events` NOTIFY channel. Tiny by design: consumers re-read the entity from the database.</summary>
public sealed record PulujEvent(
    [property: JsonPropertyName("type")] PulujEventType Type,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    /// <summary>P11: the aggregate revision the event announces (incidents); a listener drops what it already has.</summary>
    [property: JsonPropertyName("rev"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Revision = null);

[JsonConverter(typeof(JsonStringEnumConverter<PulujEventType>))]
public enum PulujEventType
{
    TrackUpserted,
    TrackClosed,
    AlertChanged,
    TargetCreated,
    /// <summary>A raw message was stored Pending (Id = raw_message_id); wakes the processor in whichever process it runs.</summary>
    RawMessageStored,
    /// <summary>P11 (ADR-0011): an incident revision was recorded (Id = incident_id, Revision set); published by the projection consumer, pushed by every API replica.</summary>
    IncidentChanged,
    /// <summary>P11: not a database event — the listener re-established its LISTEN connection (Id = generation); notifications in between were lost, clients resync.</summary>
    ListenerReconnected,
}
