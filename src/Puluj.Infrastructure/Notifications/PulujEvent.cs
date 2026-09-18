using System.Text.Json.Serialization;

namespace Puluj.Infrastructure.Notifications;

/// <summary>Payload of the `puluj_events` NOTIFY channel. Tiny by design: consumers re-read the entity from the database.</summary>
public sealed record PulujEvent(
    [property: JsonPropertyName("type")] PulujEventType Type,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    /// <summary>Optional aggregate revision for forward-compatible notifications.</summary>
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
    /// <summary>Not a database event — the listener re-established its LISTEN connection (Id = generation); notifications in between were lost, clients resync.</summary>
    ListenerReconnected,
}
