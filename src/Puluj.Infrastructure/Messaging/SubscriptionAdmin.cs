using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Operator commands on deliveries and subscriptions (ADR-0002 «drain / transfer / audited waiver», ADR-0004 W6b).
/// Service methods only — the admin endpoints and UI are P13. Every command is one transaction with actor and
/// reason, so the audit lives next to the receipts it changes; nothing here talks to the broker: a retry goes through
/// the outbox like any other publication (durable transfer, ADR-0004 §6.3).
/// </summary>
public sealed class SubscriptionAdmin(TopologyRegistrar registrar, OutboxWriter outbox, IDbContextFactory<PulujDbContext> factory, ILogger<SubscriptionAdmin> logger)
{
    /// <summary>
    /// Retry one quarantined delivery: the subscription's inbox row is removed, the receipt goes back to expected
    /// (linked to the last attempt through the new attempt's `retry_of_attempt_id` once the consumer runs), the
    /// quarantine is resolved, and a redelivery row with the stored envelope is queued straight into the subscription's
    /// queue. Returns the outbox id of the redelivery.
    /// </summary>
    public async Task<long> RetryAsync(long quarantineId, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string subscriptionId, lane, envelopeJson, eventType;
        Guid eventId;
        long? lastAttemptId;
        await using (var select = new NpgsqlCommand(
            "SELECT subscription_id, event_id, lane, envelope::text, last_attempt_id FROM processing.quarantine WHERE quarantine_id = @id AND resolved_at IS NULL FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue("id", quarantineId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"quarantine {quarantineId} not found or already resolved");
            }
            subscriptionId = reader.GetString(0);
            eventId = reader.GetGuid(1);
            lane = reader.GetString(2);
            envelopeJson = reader.GetString(3);
            lastAttemptId = reader.IsDBNull(4) ? null : reader.GetInt64(4);
        }
        using (var doc = JsonDocument.Parse(envelopeJson))
        {
            eventType = doc.RootElement.TryGetProperty("event_type", out var t) ? t.GetString() ?? "" : "";
        }
        var queue = registrar.Registry.QueueName(subscriptionId, lane);

        await Exec(conn, tx, "DELETE FROM messaging.inbox WHERE subscription_id = @s AND event_id = @e", ct, ("s", subscriptionId), ("e", eventId));
        // Attempts of the exhausted round are kept as history but no longer count: the retry starts a new round.
        await Exec(conn, tx, "UPDATE processing.attempts SET state = 'superseded' WHERE subscription_id = @s AND event_id = @e AND state IN ('failed', 'interrupted')", ct, ("s", subscriptionId), ("e", eventId));
        await Exec(conn, tx,
            """
            INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, outcome, reason, actor)
            VALUES (@e, @s, @v, now(), NULL, @reason, @actor)
            ON CONFLICT (event_id, subscription_id) DO UPDATE SET outcome = NULL, completed_at = NULL, reason = EXCLUDED.reason, actor = EXCLUDED.actor, attempt_id = NULL, expected_at = now()
            """, ct, ("e", eventId), ("s", subscriptionId), ("v", registrar.Registry.TopologyVersion), ("reason", $"retry of attempt {lastAttemptId?.ToString() ?? "-"}"), ("actor", actor));
        var outboxId = await outbox.EnqueueRedeliveryAsync(conn, tx, eventId, eventType, lane, envelopeJson, queue, ct);
        await Exec(conn, tx, "UPDATE processing.quarantine SET resolved_at = now(), resolved_by = @actor, resolution = 'retried', retry_outbox_id = @outbox WHERE quarantine_id = @id", ct, ("actor", actor), ("outbox", outboxId), ("id", quarantineId));
        await tx.CommitAsync(ct);
        logger.LogInformation("Quarantine {Quarantine} ({Subscription}/{EventId}) retried by {Actor}: outbox {Outbox} → {Queue}", quarantineId, subscriptionId, eventId, actor, outboxId, queue);
        return outboxId;
    }

    /// <summary>
    /// Audited waiver (ADR-0002): every expected delivery of the subscription (optionally only the given events) gets a
    /// terminal `waived` receipt with reason and actor; open quarantine rows of those deliveries are resolved as waived.
    /// Returns the number of deliveries waived.
    /// </summary>
    public async Task<int> WaiveAsync(string subscriptionId, string reason, string actor, IReadOnlyCollection<Guid>? eventIds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var filter = eventIds is null ? "" : " AND event_id = ANY(@ids)";
        await using var update = new NpgsqlCommand(
            $"UPDATE processing.deliveries SET outcome = 'waived', completed_at = now(), reason = @reason, actor = @actor WHERE subscription_id = @s AND outcome IS NULL{filter}", conn, tx);
        update.Parameters.AddWithValue("reason", reason);
        update.Parameters.AddWithValue("actor", actor);
        update.Parameters.AddWithValue("s", subscriptionId);
        if (eventIds is not null)
        {
            update.Parameters.AddWithValue("ids", eventIds.ToArray());
        }
        var waived = await update.ExecuteNonQueryAsync(ct);
        await using var quarantine = new NpgsqlCommand(
            $"UPDATE processing.quarantine SET resolved_at = now(), resolved_by = @actor, resolution = 'waived' WHERE subscription_id = @s AND resolved_at IS NULL{filter}", conn, tx);
        quarantine.Parameters.AddWithValue("actor", actor);
        quarantine.Parameters.AddWithValue("s", subscriptionId);
        if (eventIds is not null)
        {
            quarantine.Parameters.AddWithValue("ids", eventIds.ToArray());
        }
        await quarantine.ExecuteNonQueryAsync(ct);
        await RecordWaiverAsync(conn, tx, subscriptionId, "waiver", reason, actor, waived, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Subscription {Subscription}: {Count} expected deliveries waived by {Actor}: {Reason}", subscriptionId, waived, actor, reason);
        return waived;
    }

    /// <summary>`active` → `paused` (still expected, queue kept; ADR-0002) or back, with audit. Queues of a paused subscription stay declared.</summary>
    public async Task SetStatusAsync(string subscriptionId, string status, string reason, string actor, CancellationToken ct)
    {
        if (status is not ("active" or "paused" or "retired"))
        {
            throw new ArgumentException($"status {status} is not an operator transition", nameof(status));
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var update = new NpgsqlCommand(
            """
            UPDATE messaging.subscriptions SET status = @status, updated_at = now(),
                paused_at = CASE WHEN @status = 'paused' THEN now() ELSE paused_at END,
                activated_at = CASE WHEN @status = 'active' AND activated_at IS NULL THEN now() ELSE activated_at END
            WHERE subscription_id = @s AND topology_version = @v
            """, conn, tx);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("s", subscriptionId);
        update.Parameters.AddWithValue("v", registrar.Registry.TopologyVersion);
        if (await update.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new InvalidOperationException($"subscription {subscriptionId} v{registrar.Registry.TopologyVersion} is not registered");
        }
        await RecordWaiverAsync(conn, tx, subscriptionId, status, reason, actor, null, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Subscription {Subscription} → {Status} by {Actor}: {Reason}", subscriptionId, status, actor, reason);
    }

    private async Task RecordWaiverAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string subscriptionId, string action, string reason, string actor, int? count, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE messaging.subscriptions SET waiver = @waiver, updated_at = now() WHERE subscription_id = @s AND topology_version = @v", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("waiver", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { action, reason, actor, count, at = DateTimeOffset.UtcNow }) });
        cmd.Parameters.AddWithValue("s", subscriptionId);
        cmd.Parameters.AddWithValue("v", registrar.Registry.TopologyVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
