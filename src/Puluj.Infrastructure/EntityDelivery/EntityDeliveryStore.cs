using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.EntityExtraction;

public sealed class EntityDeliveryStore(
    IDbContextFactory<PulujDbContext> factory,
    TimeProvider clock)
{
    public async Task<ClaimedEntityDelivery?> ClaimNextAsync(string claimant, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var now = clock.GetUtcNow();

        await using var claim = new NpgsqlCommand(
            """
            WITH candidate AS (
                SELECT delivery_id
                FROM ee_delivery_queue
                WHERE status = 'pending'
                   OR (status = 'in_progress' AND lease_expires_at <= @now)
                ORDER BY enqueued_at, delivery_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            ), claimed AS (
                UPDATE ee_delivery_queue q
                SET status = 'in_progress', claimed_at = @now, lease_expires_at = @lease_expires,
                    claimed_by = @claimant, attempts = attempts + 1, last_error = NULL
                FROM candidate c
                WHERE q.delivery_id = c.delivery_id
                RETURNING q.delivery_id, q.raw_message_id, q.claimed_at
            )
            SELECT c.delivery_id, r.raw_message_id, r.source_id, s.code, r.published_at, r.received_at,
                   r.raw_text, r.raw_payload::text, r.url, c.claimed_at
            FROM claimed c
            JOIN raw_messages r ON r.raw_message_id = c.raw_message_id
            JOIN sources s ON s.source_id = r.source_id
            """, connection, transaction);
        claim.Parameters.AddWithValue("now", now);
        claim.Parameters.AddWithValue("lease_expires", now + lease);
        claim.Parameters.AddWithValue("claimant", claimant);

        ClaimedEntityDelivery? claimed = null;
        // Close the reader before anything else runs on this connection, the commit included:
        // committing while it is open throws NpgsqlOperationInProgressException on every empty poll.
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var payloadText = reader.IsDBNull(7) ? null : reader.GetString(7);
                claimed = new ClaimedEntityDelivery(0, reader.GetFieldValue<DateTimeOffset>(9), new EntityExtractionRequest(
                    reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ParsePayload(payloadText),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        if (claimed is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        await using var attempt = new NpgsqlCommand(
            """
            INSERT INTO ee_delivery_attempts (delivery_id, started_at, outcome, duration_ms)
            VALUES (@delivery_id, @started_at, 'in_progress', 0)
            RETURNING delivery_attempt_id
            """, connection, transaction);
        attempt.Parameters.AddWithValue("delivery_id", claimed.Request.DeliveryId);
        attempt.Parameters.AddWithValue("started_at", now);
        var attemptId = (long)(await attempt.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Delivery attempt insert returned no id."));
        await transaction.CommitAsync(ct);
        return claimed with { AttemptId = attemptId };
    }

    private static JsonElement? ParsePayload(string? value)
    {
        if (value is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public async Task CompleteAsync(
        ClaimedEntityDelivery delivery,
        string claimant,
        string outcome,
        int? statusCode,
        short? result,
        string? error,
        TimeSpan duration,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var now = clock.GetUtcNow();
        var durationMs = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);

        await using (var updateAttempt = new NpgsqlCommand(
            """
            UPDATE ee_delivery_attempts
            SET completed_at = @completed_at, outcome = @outcome, status_code = @status_code,
                result = @result, duration_ms = @duration_ms, error = @error
            WHERE delivery_attempt_id = @attempt_id
            """, connection, transaction))
        {
            updateAttempt.Parameters.AddWithValue("completed_at", now);
            updateAttempt.Parameters.AddWithValue("outcome", outcome);
            updateAttempt.Parameters.AddWithValue("status_code", (object?)statusCode ?? DBNull.Value);
            updateAttempt.Parameters.AddWithValue("result", (object?)result ?? DBNull.Value);
            updateAttempt.Parameters.AddWithValue("duration_ms", durationMs);
            updateAttempt.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            updateAttempt.Parameters.AddWithValue("attempt_id", delivery.AttemptId);
            await updateAttempt.ExecuteNonQueryAsync(ct);
        }

        await using (var updateQueue = new NpgsqlCommand(
            """
            UPDATE ee_delivery_queue
            SET status = @status, completed_at = @completed_at, result = @result, last_error = @error,
                lease_expires_at = NULL
            WHERE delivery_id = @delivery_id AND status = 'in_progress'
              AND claimed_by = @claimant AND claimed_at = @claimed_at
            """, connection, transaction))
        {
            updateQueue.Parameters.AddWithValue("status", outcome == "succeeded" ? "succeeded" : "failed");
            updateQueue.Parameters.AddWithValue("completed_at", now);
            updateQueue.Parameters.AddWithValue("result", (object?)result ?? DBNull.Value);
            updateQueue.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            updateQueue.Parameters.AddWithValue("delivery_id", delivery.Request.DeliveryId);
            updateQueue.Parameters.AddWithValue("claimant", claimant);
            updateQueue.Parameters.AddWithValue("claimed_at", delivery.ClaimedAt);
            var updated = await updateQueue.ExecuteNonQueryAsync(ct);
            if (updated != 1)
            {
                throw new InvalidOperationException($"Delivery {delivery.Request.DeliveryId} is no longer owned by {claimant}.");
            }
        }

        await transaction.CommitAsync(ct);
    }
}
