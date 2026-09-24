using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Notifications;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>
/// Stores a RawMessage exactly once by its identity (ADR-0003: unique (source_id, source_message_key, source_revision);
/// the content hash is only a similarity index since P04), records source latency and wakes the processors through
/// NOTIFY, so collectors and the processors may live in different processes (a processor's poll for Pending rows covers
/// a lost notification). A repeat of an already stored identity returns the existing raw id with IsNew = false.
/// An edit revision whose text equals the latest stored revision of the same message is not stored at all: Telegram
/// bumps edit_date for formatting, buttons or media captions, and each such no-op revision used to become another
/// message with the same text, processed again into the same targets (admin re-audit R02).
/// </summary>
public sealed class RawMessageIngestor(
    IDbContextFactory<PulujDbContext> factory,
    INotifyPublisher notifier,
    PulujMetrics metrics,
    TimeProvider clock,
    ILogger<RawMessageIngestor> logger)
{
    /// <summary>
    /// Inserts normally take milliseconds, but they queue behind an ACCESS EXCLUSIVE request on raw_messages (a reprocess
    /// fence, a migration). Npgsql's default 30 s would then throw and take the collector down with it; a longer wait keeps
    /// the message and its checkpoint in flight until the fence commits. ReprocessService keeps its fence far shorter than this.
    /// </summary>
    public const int CommandTimeoutSeconds = 120;

    /// <param name="announceProcessor">False while a history load is running: the message is stored Pending and the processors
    /// pick it up later in publication order, together with everything else the load brings.</param>
    public async Task<IngestResult> IngestAsync(IncomingMessage msg, string sourceCode, CancellationToken ct, bool announceProcessor = true)
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
            SELECT @source_id, @source_message_id, @key, @revision, @published_at, @received_at, @raw_text, @raw_payload, @url, @hash, 0, 0
            WHERE @revision = '0' OR NOT EXISTS (
                SELECT 1 FROM (
                    SELECT r.raw_text FROM raw_messages r
                    WHERE r.source_id = @source_id AND r.source_message_key = @key
                    ORDER BY r.raw_message_id DESC LIMIT 1) latest
                WHERE latest.raw_text IS NOT DISTINCT FROM @raw_text)
            ON CONFLICT DO NOTHING
            RETURNING raw_message_id
            """, conn) { CommandTimeout = CommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("source_id", msg.SourceId);
        cmd.Parameters.AddWithValue("source_message_id", msg.SourceMessageId);
        cmd.Parameters.AddWithValue("key", identity.SourceMessageKey);
        cmd.Parameters.AddWithValue("revision", identity.SourceRevision);
        cmd.Parameters.AddWithValue("published_at", msg.PublishedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("received_at", receivedAt);
        cmd.Parameters.Add(new NpgsqlParameter("raw_text", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)msg.RawText ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("raw_payload", NpgsqlTypes.NpgsqlDbType.Jsonb)
        {
            Value = msg.RawPayload is null ? DBNull.Value : msg.RawPayload.RootElement.GetRawText(),
        });
        cmd.Parameters.Add(new NpgsqlParameter("url", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)msg.Url ?? DBNull.Value });
        cmd.Parameters.AddWithValue("hash", hash);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long id)
        {
            metrics.RawReceived(sourceCode, receivedAt - msg.PublishedAt);
            if (announceProcessor)
            {
                await notifier.PublishAsync(new PulujEvent(PulujEventType.RawMessageStored, id, receivedAt), ct);
            }
            logger.LogDebug("RawMessage {Id} from {Source}/{SourceMessageId}", id, sourceCode, msg.SourceMessageId);
            return new IngestResult(id, true);
        }
        // Already stored (redelivery, re-read after a restart): the caller gets the existing id, nothing is published.
        // A skipped no-op edit has no row of its own, so its id stays null.
        var existing = await ExistingIdAsync(conn, null, msg.SourceId, identity, ct);
        return new IngestResult(existing, false);
    }

    /// <summary>
    /// Stores a whole page of messages in one round trip and one transaction (unnest + ON CONFLICT DO NOTHING), then
    /// runs <paramref name="inSameTransaction"/> on the same context before commit — a collector puts its checkpoint
    /// there, so the cursor can never move past a message that was not stored. Results align with the input; an
    /// already stored identity yields IsNew = false without looking up its existing id.
    /// </summary>
    public async Task<IReadOnlyList<IngestResult>> IngestBatchAsync(IReadOnlyList<IncomingMessage> msgs, string sourceCode, CancellationToken ct,
        bool announceProcessor = true, Func<PulujDbContext, CancellationToken, Task>? inSameTransaction = null)
    {
        var receivedAt = clock.GetUtcNow();
        var n = msgs.Count;
        var identities = new SourceIdentity[n];
        var sourceIds = new List<int>(n);
        var messageIds = new List<string>(n);
        var keys = new List<string>(n);
        var revisions = new List<string>(n);
        var publishedAt = new List<DateTime>(n);
        var texts = new List<string?>(n);
        var payloads = new List<string?>(n);
        var urls = new List<string?>(n);
        var hashes = new List<string>(n);
        var pageTexts = new Dictionary<(int, string), string?>();
        for (var i = 0; i < n; i++)
        {
            var msg = msgs[i];
            identities[i] = Identity(msg);
            // The SQL guard cannot see rows of the same statement: drop a no-op edit of a message earlier in this page.
            var pageKey = (msg.SourceId, identities[i].SourceMessageKey);
            if (identities[i].SourceRevision != "0" && pageTexts.TryGetValue(pageKey, out var pageText) && pageText == msg.RawText)
            {
                continue;
            }
            pageTexts[pageKey] = msg.RawText;
            var payload = msg.RawPayload?.RootElement.GetRawText();
            sourceIds.Add(msg.SourceId);
            messageIds.Add(msg.SourceMessageId);
            keys.Add(identities[i].SourceMessageKey);
            revisions.Add(identities[i].SourceRevision);
            publishedAt.Add(msg.PublishedAt.UtcDateTime);
            texts.Add(msg.RawText);
            payloads.Add(payload);
            urls.Add(msg.Url);
            hashes.Add(ComputeHash(sourceCode, msg.RawText, payload));
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var newIds = new Dictionary<SourceIdentity, long>(n);
        if (sourceIds.Count > 0)
        {
            // ORDER BY the array position so raw_message_id stays monotonic within the page, like one insert per message.
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, raw_payload, url, hash, processing_status, attempts)
                SELECT m.source_id, m.source_message_id, m.key, m.revision, m.published_at, @received_at, m.raw_text, m.raw_payload::jsonb, m.url, m.hash, 0, 0
                FROM unnest(@source_ids, @source_message_ids, @keys, @revisions, @published_at, @raw_texts, @raw_payloads, @urls, @hashes)
                    WITH ORDINALITY AS m(source_id, source_message_id, key, revision, published_at, raw_text, raw_payload, url, hash, ord)
                WHERE m.revision = '0' OR NOT EXISTS (
                    SELECT 1 FROM (
                        SELECT r.raw_text FROM raw_messages r
                        WHERE r.source_id = m.source_id AND r.source_message_key = m.key
                        ORDER BY r.raw_message_id DESC LIMIT 1) latest
                    WHERE latest.raw_text IS NOT DISTINCT FROM m.raw_text)
                ORDER BY m.ord
                ON CONFLICT DO NOTHING
                RETURNING source_message_key, source_revision, raw_message_id
                """, conn, (NpgsqlTransaction)tx.GetDbTransaction()) { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("received_at", receivedAt);
            cmd.Parameters.AddWithValue("source_ids", sourceIds.ToArray());
            cmd.Parameters.AddWithValue("source_message_ids", messageIds.ToArray());
            cmd.Parameters.AddWithValue("keys", keys.ToArray());
            cmd.Parameters.AddWithValue("revisions", revisions.ToArray());
            cmd.Parameters.Add(new NpgsqlParameter("published_at", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = publishedAt.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("raw_texts", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = texts.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("raw_payloads", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = payloads.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("urls", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = urls.ToArray() });
            cmd.Parameters.AddWithValue("hashes", hashes.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                newIds[new SourceIdentity(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
            }
        }
        if (inSameTransaction is not null)
        {
            await inSameTransaction(db, ct);
        }
        await tx.CommitAsync(ct);

        var results = new IngestResult[n];
        for (var i = 0; i < n; i++)
        {
            // Remove() so a repeated identity within the page counts as new only once.
            if (newIds.Remove(identities[i], out var id))
            {
                results[i] = new IngestResult(id, true);
                metrics.RawReceived(sourceCode, receivedAt - msgs[i].PublishedAt);
                if (announceProcessor)
                {
                    await notifier.PublishAsync(new PulujEvent(PulujEventType.RawMessageStored, id, receivedAt), ct);
                }
                logger.LogDebug("RawMessage {Id} from {Source}/{SourceMessageId}", id, sourceCode, msgs[i].SourceMessageId);
            }
            else
            {
                results[i] = new IngestResult(null, false);
            }
        }
        return results;
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
