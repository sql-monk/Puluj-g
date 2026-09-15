using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Transport event envelope (plan §5.1, `contracts/messaging/schemas/envelope.schema.json`). Serialized 1:1 into the
/// outbox, the broker message body and the archive; unknown extra fields of an incoming message are ignored (additive
/// compatibility, ADR-0003), so consumers never throw on a newer MINOR.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("event_id")] public Guid EventId { get; set; }
    [JsonPropertyName("event_type")] public required string EventType { get; set; }
    [JsonPropertyName("schema_version")] public required string SchemaVersion { get; set; }
    [JsonPropertyName("producer")] public required string Producer { get; set; }
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    /// <summary>First publication of this event id = the moment it was committed to the outbox; a republish never changes it (§15.2).</summary>
    [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("source_id")] public int? SourceId { get; set; }
    [JsonPropertyName("source_message_key")] public string? SourceMessageKey { get; set; }
    [JsonPropertyName("source_revision")] public string? SourceRevision { get; set; }
    [JsonPropertyName("raw_message_id")] public long? RawMessageId { get; set; }
    [JsonPropertyName("correlation_id")] public Guid CorrelationId { get; set; }
    /// <summary>Event that caused this one; null only for `ingress.received`. A bridge-produced `raw.stored` (no ingress event, P03) is its own cause: causation_id == event_id marks a root (ADR-0003).</summary>
    [JsonPropertyName("causation_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Guid? CausationId { get; set; } // required by the schema, null for a root
    [JsonPropertyName("traceparent")] public required string Traceparent { get; set; }
    [JsonPropertyName("processing_run_id")] public Guid ProcessingRunId { get; set; }
    [JsonPropertyName("pipeline_version")] public required string PipelineVersion { get; set; }
    [JsonPropertyName("topology_version")] public int TopologyVersion { get; set; }
    [JsonPropertyName("lane")] public required string Lane { get; set; }
    [JsonPropertyName("partition_key")] public string? PartitionKey { get; set; }
    [JsonPropertyName("aggregate_id")] public string? AggregateId { get; set; }
    [JsonPropertyName("aggregate_revision")] public long? AggregateRevision { get; set; }
    [JsonPropertyName("payload")] public JsonObject? Payload { get; set; }
    [JsonPropertyName("payload_ref")] public JsonObject? PayloadRef { get; set; }

    /// <summary>Original message body when parsed from the broker (kept for the archive so additive fields of a newer MINOR survive); null for locally built envelopes.</summary>
    [JsonIgnore] public string? RawJson { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The body as received, or the serialized form for envelopes built here.</summary>
    public string ToArchiveJson() => RawJson ?? ToJson();

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>Parses a message body; returns null when it is not a JSON object or lacks the required identity fields (→ quarantine `invalid_payload`).</summary>
    public static Envelope? TryParse(ReadOnlySpan<byte> utf8, out string? error)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(utf8, JsonOptions);
            if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrEmpty(envelope.EventType) || string.IsNullOrEmpty(envelope.Lane))
            {
                error = "envelope without event_id/event_type/lane";
                return null;
            }
            envelope.RawJson = System.Text.Encoding.UTF8.GetString(utf8);
            error = null;
            return envelope;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
