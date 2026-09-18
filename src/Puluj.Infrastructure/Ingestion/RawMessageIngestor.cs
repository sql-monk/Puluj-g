using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>
/// Stores a RawMessage exactly once by its identity (ADR-0003: unique (source_id, source_message_key, source_revision);
/// the content hash is only a similarity index since P04), records source latency and wakes the processors through
/// NOTIFY, so collectors and the processors may live in different processes (a processor's poll for Pending rows covers
/// a lost notification). A repeat of an already stored identity returns the existing raw id with IsNew = false.
/// </summary>
public sealed class RawMessageIngestor(
    IDbContextFactory<PulujDbContext> factory,
    INotifyPublisher notifier,
    PulujMetrics metrics,
    TimeProvider clock,
    ILogger<RawMessageIngestor> logger)
{
    /// <param name="enqueue">False while a history load is running: the message is stored Pending and the processors
    /// pick it up later in publication order, together with everything else the load brings.</param>
    public async Task<IngestResult> IngestAsync(IncomingMessage msg, string sourceCode, CancellationToken ct, bool enqueue = true)
    {
        var receivedAt = clock.GetUtcNow();
        var hash = ComputeHash(sourceCode, msg.RawText, msg.RawPayload?.RootElement.GetRawText());
        var identity = Identity(msg);

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, raw_payload, url, hash, processing_status, attempts)
            VALUES (@source_id, @source_message_id, @key, @revision, @published_at, @received_at, @raw_text, @raw_payload, @url, @hash, 0, 0)
            ON CONFLICT DO NOTHING
            RETURNING raw_message_id
            """, conn);
        cmd.Parameters.AddWithValue("source_id", msg.SourceId);
        cmd.Parameters.AddWithValue("source_message_id", msg.SourceMessageId);
        cmd.Parameters.AddWithValue("key", identity.SourceMessageKey);
        cmd.Parameters.AddWithValue("revision", identity.SourceRevision);
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
            metrics.RawReceived(sourceCode, receivedAt - msg.PublishedAt);
            if (enqueue)
            {
                await notifier.PublishAsync(new PulujEvent(PulujEventType.RawMessageStored, id, receivedAt), ct);
            }
            logger.LogDebug("RawMessage {Id} from {Source}/{SourceMessageId}", id, sourceCode, msg.SourceMessageId);
            return new IngestResult(id, true);
        }
        // Already stored (redelivery, re-read after a restart): the caller gets the existing id, nothing is published.
        var existing = await ExistingIdAsync(conn, null, msg.SourceId, identity, ct);
        return new IngestResult(existing, false);
    }

    /// <summary>Identity from the message, or derived from the legacy id by the rules of ADR-0003 (identity-cases fixture).</summary>
    public static SourceIdentity Identity(IncomingMessage msg) =>
        msg.SourceMessageKey is { Length: > 0 } key ? new SourceIdentity(key, msg.SourceRevision is { Length: > 0 } rev ? rev : "0") : SourceIdentity.FromLegacy(msg.SourceMessageId);

    public static async Task<long?> ExistingIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int sourceId, SourceIdentity identity, CancellationToken ct)
    {
        await using var select = new NpgsqlCommand("SELECT raw_message_id FROM raw_messages WHERE source_id = @s AND source_message_key = @k AND source_revision = @r", conn, tx);
        select.Parameters.AddWithValue("s", sourceId);
        select.Parameters.AddWithValue("k", identity.SourceMessageKey);
        select.Parameters.AddWithValue("r", identity.SourceRevision);
        return await select.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    /// <summary>Content hash without the source message id: a similarity index (copies, forwards); it no longer prevents a store (P04).</summary>
    public static string ComputeHash(string sourceCode, string? text, string? payload)
    {
        var material = string.Join('\n', sourceCode, text, payload);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
