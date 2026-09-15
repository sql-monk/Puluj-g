using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Builds the `raw.stored` envelope (`schemas/events/raw.stored.schema.json`) for the DB-first bridge: the collector
/// stores the raw row directly (no `ingress.received` until P04) and the same transaction commits this event.
/// Identity follows ADR-0003 (<see cref="SourceIdentity"/>); the correlation id is deterministic per source post.
/// </summary>
public static class RawStoredEnvelope
{
    public const string EventType = "raw.stored";
    public const string SchemaVersion = "1.0";

    public static Envelope Create(
        string producer,
        Guid processingRunId,
        string pipelineVersion,
        string lane,
        long rawMessageId,
        int sourceId,
        string sourceCode,
        string legacySourceMessageId,
        DateTimeOffset sourcePublishedAt,
        DateTimeOffset receivedAt,
        DateTimeOffset storedAt,
        string contentHash,
        bool isNew,
        string? text,
        bool hasPayload,
        string? url)
    {
        var identity = SourceIdentity.FromLegacy(legacySourceMessageId);
        var payload = new JsonObject
        {
            ["raw_message_id"] = rawMessageId,
            ["source_code"] = sourceCode,
            ["source_published_at"] = Iso(sourcePublishedAt),
            ["received_at"] = Iso(receivedAt),
            ["stored_at"] = Iso(storedAt),
            ["content_hash"] = contentHash,
            ["is_new"] = isNew,
            ["has_text"] = !string.IsNullOrEmpty(text),
            ["has_payload"] = hasPayload,
            ["text_length"] = text?.Length ?? 0,
        };
        if (!string.IsNullOrEmpty(url))
        {
            payload["url"] = url;
        }
        return new Envelope
        {
            EventType = EventType,
            SchemaVersion = SchemaVersion,
            Producer = producer,
            OccurredAt = storedAt.ToUniversalTime(),
            SourceId = sourceId,
            SourceMessageKey = identity.SourceMessageKey,
            SourceRevision = identity.SourceRevision,
            RawMessageId = rawMessageId,
            CorrelationId = SourceIdentity.CorrelationId(sourceId, identity.SourceMessageKey),
            CausationId = null, // filled by the outbox writer: a bridge root is its own cause
            Traceparent = CurrentTraceparent(),
            ProcessingRunId = processingRunId,
            PipelineVersion = pipelineVersion,
            Lane = lane,
            Payload = payload,
        };
    }

    /// <summary>`2026-09-15T10:00:00.0000000Z` — the schema's date-time pattern, UTC.</summary>
    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O");

    /// <summary>W3C traceparent of the current activity, or a fresh trace when nothing is being traced.</summary>
    public static string CurrentTraceparent()
    {
        if (Activity.Current is { } activity && activity.IdFormat == ActivityIdFormat.W3C)
        {
            return $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}";
        }
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return $"00-{Convert.ToHexStringLower(bytes[..16])}-{Convert.ToHexStringLower(bytes[16..])}-01";
    }
}
