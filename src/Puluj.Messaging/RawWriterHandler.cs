using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging;

/// <summary>
/// The `raw-writer` subscription (plan §4, ADR-0004 W1a): stores the original of an `ingress.received` exactly once by
/// its identity `(source_id, source_message_key, source_revision)` and, in the same transaction, queues `raw.stored`
/// with `is_new` — true for the first store, false for a redelivery or a re-read after a collector restart (the
/// existing raw id is reported either way). The content hash comes from the collector (`payload.content_hash`), so
/// direct and ingress stores agree on it. After the commit a NOTIFY wakes the legacy processors for live posts; a
/// history-lane row stays Pending until the rebuild (plan §11).
/// </summary>
public sealed class RawWriterHandler(IOptions<MessagingOptions> options, INotifyPublisher notifier, PulujMetrics metrics, TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "raw-writer";

    public string SubscriptionId => Subscription;
    /// <summary>`raw-writer@{instance}` — the `producer` of the raw.stored envelopes (who stored the row); set by DI.</summary>
    public string Producer { get; set; } = Subscription;

    public Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct) => Task.FromResult<object?>(null);

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", "ingress.received without payload");
        var sourceId = envelope.SourceId ?? throw new PermanentDeliveryException("invalid_payload", "ingress.received without source_id");
        var sourceCode = Required(payload, "source_code");
        var key = envelope.SourceMessageKey ?? Required(payload, "source_message_key");
        var revision = envelope.SourceRevision ?? Required(payload, "source_revision");
        var legacyId = payload["legacy_source_message_id"]?.GetValue<string>() ?? (revision == "0" ? key : $"{key}:{revision}");
        var publishedAt = ParseTime(payload, "source_published_at") ?? envelope.OccurredAt;
        var receivedAt = ParseTime(payload, "received_at") ?? envelope.PublishedAt;
        var text = payload["text"]?.GetValue<string>();
        var rawPayload = payload["raw_payload"]?.ToJsonString();
        var url = payload["url"]?.GetValue<string>();
        var hash = payload["content_hash"]?.GetValue<string>() ?? RawMessageIngestor.ComputeHash(sourceCode, text, rawPayload);
        var storedAt = clock.GetUtcNow();

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, raw_payload, url, hash, processing_status, attempts)
            VALUES (@source_id, @legacy, @key, @revision, @published_at, @received_at, @raw_text, @raw_payload, @url, @hash, 0, 0)
            ON CONFLICT DO NOTHING
            RETURNING raw_message_id
            """, conn, tx);
        // ReprocessService.ResetAsync holds raw_messages exclusively for the whole rebuild: wait, do not fail (a failure
        // would count as an attempt and end in quarantine).
        insert.CommandTimeout = (int)options.Value.RawWriter.InsertTimeout.TotalSeconds;
        insert.Parameters.AddWithValue("source_id", sourceId);
        insert.Parameters.AddWithValue("legacy", legacyId);
        insert.Parameters.AddWithValue("key", key);
        insert.Parameters.AddWithValue("revision", revision);
        insert.Parameters.AddWithValue("published_at", publishedAt.ToUniversalTime());
        insert.Parameters.AddWithValue("received_at", receivedAt.ToUniversalTime());
        insert.Parameters.AddWithValue("raw_text", (object?)text ?? DBNull.Value);
        insert.Parameters.Add(new NpgsqlParameter("raw_payload", NpgsqlDbType.Jsonb) { Value = (object?)rawPayload ?? DBNull.Value });
        insert.Parameters.AddWithValue("url", (object?)url ?? DBNull.Value);
        insert.Parameters.AddWithValue("hash", hash);
        var inserted = await insert.ExecuteScalarAsync(ct) as long?;
        var isNew = inserted is not null;
        var rawId = inserted ?? await RawMessageIngestor.ExistingIdAsync(conn, tx, sourceId, new SourceIdentity(key, revision), ct)
            ?? throw new InvalidOperationException($"raw_messages: identity {sourceId}/{key}/{revision} neither inserted nor found");

        var stored = RawStoredEnvelope.Create(
            Producer, envelope.ProcessingRunId, envelope.PipelineVersion, envelope.Lane, rawId, sourceId, sourceCode, legacyId,
            publishedAt, receivedAt, storedAt, hash, isNew, text, rawPayload is not null, url);
        stored.SourceMessageKey = key; // the identity of the row, as the collector declared it (not re-derived from the legacy id)
        stored.SourceRevision = revision;
        stored.CorrelationId = envelope.CorrelationId;
        stored.CausationId = envelope.EventId;
        stored.Traceparent = envelope.Traceparent;

        return new DeliveryResult(isNew ? "completed" : "noop", isNew ? null : "already stored", [stored])
        {
            AfterCommit = async token =>
            {
                if (isNew)
                {
                    metrics.RawReceived(sourceCode, receivedAt - publishedAt);
                    if (envelope.Lane == "live")
                    {
                        await notifier.PublishAsync(new PulujEvent(PulujEventType.RawMessageStored, rawId, storedAt), token);
                    }
                }
            },
        };
    }

    private static string Required(JsonObject payload, string name) =>
        payload[name]?.GetValue<string>() ?? throw new PermanentDeliveryException("invalid_payload", $"ingress.received payload without {name}");

    private static DateTimeOffset? ParseTime(JsonObject payload, string name) =>
        payload[name] is { } node && DateTimeOffset.TryParse(node.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
