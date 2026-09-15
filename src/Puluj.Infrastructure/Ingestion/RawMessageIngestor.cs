using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>
/// Stores a RawMessage exactly once (spec §5 idempotency: unique (source, source_message_id) and unique hash),
/// records source latency and wakes the processors through NOTIFY, so collectors and the processors may live in
/// different processes (a processor's poll for Pending rows covers a lost notification).
/// With <c>Messaging:Outbox:Enabled</c> it is also the DB-first bridge of plan §11: the raw row, the `raw.stored`
/// outbox event and the expected deliveries commit in one transaction; the relay publishes afterwards (ADR-0004 W2).
/// A duplicate (ON CONFLICT) publishes nothing until P04 gives collectors `raw.stored{is_new:false}` semantics.
/// </summary>
public sealed class RawMessageIngestor(
    IDbContextFactory<PulujDbContext> factory,
    INotifyPublisher notifier,
    OutboxWriter outbox,
    PulujMetrics metrics,
    TimeProvider clock,
    ILogger<RawMessageIngestor> logger)
{
    /// <summary>`producer` of the bridge envelopes: `raw-writer@{instance}` (topology.json: raw.stored is produced by raw-writer).</summary>
    public string Producer { get; set; } = "raw-writer";

    /// <param name="enqueue">False while a history load is running: the message is stored Pending and the processors
    /// pick it up later in publication order, together with everything else the load brings. The bridge event then
    /// goes to lane `history`.</param>
    public async Task<IngestResult> IngestAsync(IncomingMessage msg, string sourceCode, CancellationToken ct, bool enqueue = true)
    {
        var receivedAt = clock.GetUtcNow();
        var hash = ComputeHash(sourceCode, msg.RawText, msg.RawPayload?.RootElement.GetRawText());

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        // The outbox rows must commit with the raw row or not at all; without the bridge the insert stays autocommit.
        await using var tx = outbox.Enabled ? await conn.BeginTransactionAsync(ct) : null;
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO raw_messages (source_id, source_message_id, published_at, received_at, raw_text, raw_payload, url, hash, processing_status, attempts)
            VALUES (@source_id, @source_message_id, @published_at, @received_at, @raw_text, @raw_payload, @url, @hash, 0, 0)
            ON CONFLICT DO NOTHING
            RETURNING raw_message_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("source_id", msg.SourceId);
        cmd.Parameters.AddWithValue("source_message_id", msg.SourceMessageId);
        cmd.Parameters.AddWithValue("published_at", msg.PublishedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("received_at", receivedAt);
        cmd.Parameters.AddWithValue("raw_text", (object?)msg.RawText ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("raw_payload", NpgsqlTypes.NpgsqlDbType.Jsonb)
        {
            Value = msg.RawPayload is null ? DBNull.Value : msg.RawPayload.RootElement.GetRawText(),
        });
        cmd.Parameters.AddWithValue("url", (object?)msg.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hash", hash);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long id)
        {
            if (tx is not null)
            {
                var lane = enqueue ? "live" : "history";
                var runId = await outbox.Runs.GetOpenRunAsync(conn, tx, lane, ct);
                var envelope = RawStoredEnvelope.Create(
                    Producer, runId, outbox.Runs.PipelineVersion, lane, id, msg.SourceId, sourceCode, msg.SourceMessageId,
                    msg.PublishedAt, receivedAt, receivedAt, hash, isNew: true, msg.RawText, msg.RawPayload is not null, msg.Url);
                var written = await outbox.EnqueueAsync(conn, tx, envelope, ct);
                await tx.CommitAsync(ct);
                logger.LogDebug("Outbox {EventId} raw.stored/{Lane} for raw {Id}: expected {Subscriptions}", written.EventId, lane, id, string.Join(",", written.ExpectedSubscriptions));
            }
            metrics.RawReceived(sourceCode, receivedAt - msg.PublishedAt);
            if (enqueue)
            {
                await notifier.PublishAsync(new PulujEvent(PulujEventType.RawMessageStored, id, receivedAt), ct);
            }
            logger.LogDebug("RawMessage {Id} from {Source}/{SourceMessageId}", id, sourceCode, msg.SourceMessageId);
            return new IngestResult(id, true);
        }
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
        return new IngestResult(null, false);
    }

    /// <summary>Content hash without the source message id, so identical content re-published under a new id is still deduplicated.</summary>
    public static string ComputeHash(string sourceCode, string? text, string? payload)
    {
        var material = string.Join('\n', sourceCode, text, payload);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
