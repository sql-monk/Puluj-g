using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Messaging;

/// <summary>Source cursor written by the same transaction as the ingress event (plan §6.1); null members keep the stored value.</summary>
public sealed record CollectorCheckpoint(string? LastSourceMessageId = null, DateTimeOffset? LastMessageAt = null, JsonDocument? Cursor = null);

public sealed record IngressPublished(Guid EventId, Guid CorrelationId, string Lane, SourceIdentity Identity, string ContentHash);

/// <summary>
/// The durable producer outbox of the collectors (plan §6.1, ADR-0004 W1a/W1b): one transaction commits the
/// `ingress.received` envelope (with its expected deliveries: raw-writer, archive) and the source checkpoint in
/// `collector_states`, so a crash before the commit leaves neither — the collector re-reads from the old checkpoint
/// and the raw-writer deduplicates by identity — and a crash after it is the relay's problem. The raw row is written
/// later by the raw-writer subscription; the content hash is computed here, on the original JSON, so direct and
/// ingress stores of the same post agree on it (jsonb would normalise the payload on the way).
/// </summary>
public sealed class IngressWriter(
    OutboxWriter outbox,
    IDbContextFactory<PulujDbContext> factory,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    ILogger<IngressWriter> logger)
{
    public const string EventType = "ingress.received";
    public const string SchemaVersion = "1.0";
    public const string Subscription = "raw-writer";

    public bool Enabled => options.Value.Ingress.Enabled;
    /// <summary>`collectors@{instance}` (topology.json: ingress.received is produced by the collectors role).</summary>
    public string Producer { get; set; } = "collectors";
    /// <summary>`collector.instance` of the payload; defaults to the machine name.</summary>
    public string Instance { get; set; } = Environment.MachineName.ToLowerInvariant();

    /// <param name="live">False for a history load: lane `history`, stored Pending without a wake-up.</param>
    public async Task<IngressPublished> PublishAsync(IncomingMessage msg, string sourceCode, string collectorName, CollectorCheckpoint? checkpoint, bool live, CancellationToken ct)
    {
        var receivedAt = clock.GetUtcNow();
        var lane = live ? "live" : "history";
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var runId = await outbox.Runs.GetOpenRunAsync(conn, tx, lane, ct);
        var (envelope, identity, contentHash) = BuildEnvelope(msg, sourceCode, collectorName, checkpoint, lane, runId, outbox.Runs.PipelineVersion, $"{Producer}@{Instance}", Instance, receivedAt);
        var written = await outbox.EnqueueAsync(conn, tx, envelope, ct);
        if (checkpoint is not null)
        {
            await CheckpointAsync(conn, tx, msg.SourceId, checkpoint, ct);
        }
        await tx.CommitAsync(ct);
        logger.LogDebug("Ingress {EventId} {Source}/{Key}/{Revision} lane {Lane}: expected {Subscriptions}", written.EventId, sourceCode, identity.SourceMessageKey, identity.SourceRevision, lane, string.Join(",", written.ExpectedSubscriptions));
        return new IngressPublished(written.EventId, envelope.CorrelationId, lane, identity, contentHash);
    }

    /// <summary>The `ingress.received` envelope (root of the chain) and what the raw-writer needs to agree with a direct store: identity and content hash.</summary>
    public static (Envelope Envelope, SourceIdentity Identity, string ContentHash) BuildEnvelope(
        IncomingMessage msg, string sourceCode, string collectorName, CollectorCheckpoint? checkpoint, string lane, Guid runId, string pipelineVersion, string producer, string instance, DateTimeOffset receivedAt)
    {
        var identity = RawMessageIngestor.Identity(msg);
        var rawPayload = msg.RawPayload?.RootElement.GetRawText();
        var contentHash = RawMessageIngestor.ComputeHash(sourceCode, msg.RawText, rawPayload);
        var payload = new JsonObject
        {
            ["source_code"] = sourceCode,
            ["source_message_key"] = identity.SourceMessageKey,
            ["source_revision"] = identity.SourceRevision,
            ["legacy_source_message_id"] = msg.SourceMessageId,
            ["source_published_at"] = RawStoredEnvelope.Iso(msg.PublishedAt),
            ["received_at"] = RawStoredEnvelope.Iso(receivedAt),
            ["content_hash"] = contentHash,
            ["collector"] = new JsonObject { ["name"] = collectorName, ["version"] = pipelineVersion, ["instance"] = instance },
        };
        if (!string.IsNullOrEmpty(msg.Url))
        {
            payload["url"] = msg.Url;
        }
        if (msg.RawText is not null)
        {
            payload["text"] = msg.RawText;
        }
        if (rawPayload is not null)
        {
            payload["raw_payload"] = JsonNode.Parse(rawPayload);
        }
        if (checkpoint is not null)
        {
            var cp = new JsonObject();
            if (checkpoint.LastSourceMessageId is not null)
            {
                cp["last_source_message_id"] = checkpoint.LastSourceMessageId;
            }
            if (checkpoint.LastMessageAt is not null)
            {
                cp["last_message_at"] = RawStoredEnvelope.Iso(checkpoint.LastMessageAt.Value);
            }
            if (checkpoint.Cursor is not null)
            {
                cp["cursor"] = JsonNode.Parse(checkpoint.Cursor.RootElement.GetRawText());
            }
            payload["checkpoint"] = cp;
        }
        var envelope = new Envelope
        {
            EventType = EventType,
            SchemaVersion = SchemaVersion,
            Producer = producer,
            OccurredAt = msg.PublishedAt.ToUniversalTime(),
            SourceId = msg.SourceId,
            SourceMessageKey = identity.SourceMessageKey,
            SourceRevision = identity.SourceRevision,
            RawMessageId = null,
            CorrelationId = SourceIdentity.CorrelationId(msg.SourceId, identity.SourceMessageKey),
            CausationId = null, // root of the chain (envelope schema: null only for ingress.received)
            Traceparent = RawStoredEnvelope.CurrentTraceparent(),
            ProcessingRunId = runId,
            PipelineVersion = pipelineVersion,
            Lane = lane,
            Payload = payload,
        };
        return (envelope, identity, contentHash);
    }

    /// <summary>Upsert of the checkpoint: only the given members change (a live handler and a history page may write the same row).</summary>
    public static async Task CheckpointAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int sourceId, CollectorCheckpoint checkpoint, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO collector_states (source_id, last_polled_at, last_success_at, last_error, consecutive_failures, last_source_message_id, last_message_at, cursor)
            VALUES (@s, now(), now(), NULL, 0, @id, @at, @cursor)
            ON CONFLICT (source_id) DO UPDATE SET
                last_polled_at = now(), last_success_at = now(), last_error = NULL, consecutive_failures = 0,
                last_source_message_id = COALESCE(EXCLUDED.last_source_message_id, collector_states.last_source_message_id),
                last_message_at = CASE
                    WHEN EXCLUDED.last_message_at IS NULL THEN collector_states.last_message_at
                    WHEN collector_states.last_message_at IS NULL OR EXCLUDED.last_message_at > collector_states.last_message_at THEN EXCLUDED.last_message_at
                    ELSE collector_states.last_message_at END,
                cursor = COALESCE(EXCLUDED.cursor, collector_states.cursor)
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", sourceId);
        cmd.Parameters.AddWithValue("id", (object?)checkpoint.LastSourceMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", (object?)checkpoint.LastMessageAt?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("cursor", NpgsqlDbType.Jsonb) { Value = (object?)checkpoint.Cursor?.RootElement.GetRawText() ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Waits until every `ingress.received` of the given sources has a raw-writer receipt (the raw rows exist), so a
    /// history load can rebuild in order (plan §11). Counts expected deliveries, which exist from the moment of the
    /// enqueue — unpublished outbox rows are covered. False on timeout.
    /// </summary>
    public async Task<bool> WaitForDrainAsync(IReadOnlyCollection<int> sourceIds, TimeSpan? timeout, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + (timeout ?? options.Value.Ingress.DrainTimeout);
        while (true)
        {
            var pending = await PendingRawWritesAsync(sourceIds, ct);
            if (pending == 0)
            {
                return true;
            }
            if (clock.GetUtcNow() >= deadline)
            {
                logger.LogError("Ingress drain timeout: {Pending} ingress.received of sources {Sources} still without a raw-writer receipt", pending, string.Join(",", sourceIds));
                return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public async Task<long> PendingRawWritesAsync(IReadOnlyCollection<int> sourceIds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM processing.deliveries d
            JOIN messaging.outbox o ON o.event_id = d.event_id AND o.target_queue IS NULL
            WHERE d.subscription_id = @sub AND d.outcome IS NULL AND o.event_type = @type
              AND (o.envelope->>'source_id')::int = ANY(@sources)
            """, conn);
        cmd.Parameters.AddWithValue("sub", Subscription);
        cmd.Parameters.AddWithValue("type", EventType);
        cmd.Parameters.AddWithValue("sources", sourceIds.ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
