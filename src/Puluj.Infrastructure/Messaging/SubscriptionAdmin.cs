using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Puluj.Domain.Entities.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Operator commands on deliveries and subscriptions (ADR-0002 «drain / transfer / audited waiver», ADR-0004 W6b).
/// Service methods only — the admin endpoints and UI are P13. Every command is one transaction with actor and
/// reason, so the audit lives next to the receipts it changes; nothing here talks to the broker: a retry goes through
/// the outbox like any other publication (durable transfer, ADR-0004 §6.3). P13 (ADR-0012): every command also leaves a
/// row in `messaging.control_audit`, and lane-level runtime state (`SetLaneStateAsync`) is what the consumers poll.
/// </summary>
public sealed class SubscriptionAdmin(TopologyRegistrar registrar, OutboxWriter outbox, IDbContextFactory<PulujDbContext> factory, ILogger<SubscriptionAdmin> logger)
{
    /// <summary>Column widths of `subscription_lanes`/`control_audit` (actor, reason).</summary>
    public const int MaxActor = 128, MaxReason = 1000;

    /// <summary>
    /// Retry one quarantined delivery: the subscription's inbox row is removed, the receipt goes back to expected
    /// (linked to the last attempt through the new attempt's `retry_of_attempt_id` once the consumer runs), the
    /// quarantine is resolved, and a redelivery row with the stored envelope is queued straight into the subscription's
    /// queue. Returns the outbox id of the redelivery.
    /// </summary>
    public async Task<long> RetryAsync(long quarantineId, string actor, CancellationToken ct) => await RetryAsync(quarantineId, actor, null, ct);

    public async Task<long> RetryAsync(long quarantineId, string actor, string? reason, CancellationToken ct)
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
            INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, outcome, reason, actor, lane)
            VALUES (@e, @s, @v, now(), NULL, @reason, @actor, @lane)
            ON CONFLICT (event_id, subscription_id) DO UPDATE SET outcome = NULL, completed_at = NULL, reason = EXCLUDED.reason, actor = EXCLUDED.actor, attempt_id = NULL, expected_at = now(), lane = COALESCE(processing.deliveries.lane, EXCLUDED.lane)
            """, ct, ("e", eventId), ("s", subscriptionId), ("v", registrar.Registry.TopologyVersion), ("reason", $"retry of attempt {lastAttemptId?.ToString() ?? "-"}"), ("actor", actor), ("lane", lane));
        var outboxId = await outbox.EnqueueRedeliveryAsync(conn, tx, eventId, eventType, lane, envelopeJson, queue, ct);
        await Exec(conn, tx, "UPDATE processing.quarantine SET resolved_at = now(), resolved_by = @actor, resolution = 'retried', retry_outbox_id = @outbox WHERE quarantine_id = @id", ct, ("actor", actor), ("outbox", outboxId), ("id", quarantineId));
        await AuditAsync(conn, tx, "retry", subscriptionId, lane, actor, reason ?? $"retry of quarantine {quarantineId}", new { quarantineId, eventId, outboxId, lastAttemptId }, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Quarantine {Quarantine} ({Subscription}/{EventId}) retried by {Actor}: outbox {Outbox} → {Queue}", quarantineId, subscriptionId, eventId, actor, outboxId, queue);
        return outboxId;
    }

    /// <summary>
    /// Audited waiver (ADR-0002): every expected delivery of the subscription (optionally only the given events) gets a
    /// terminal `waived` receipt with reason and actor; open quarantine rows of those deliveries are resolved as waived,
    /// and their `quarantined` receipts become `waived` too (the same quarantined → terminal rule as `WriteReceiptAsync`),
    /// so the root no longer needs attention. Returns the number of deliveries waived.
    /// </summary>
    public async Task<int> WaiveAsync(string subscriptionId, string reason, string actor, IReadOnlyCollection<Guid>? eventIds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var filter = eventIds is null ? "" : " AND event_id = ANY(@ids)";
        await using var update = new NpgsqlCommand(
            $"UPDATE processing.deliveries SET outcome = 'waived', completed_at = now(), reason = @reason, actor = @actor WHERE subscription_id = @s AND (outcome IS NULL OR outcome = 'quarantined'){filter}", conn, tx);
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
        await AuditAsync(conn, tx, "waive", subscriptionId, null, actor, reason, new { waived, eventIds = eventIds?.Count }, ct);
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
        await AuditAsync(conn, tx, $"status:{status}", subscriptionId, null, actor, reason, null, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Subscription {Subscription} → {Status} by {Actor}: {Reason}", subscriptionId, status, actor, reason);
    }

    /// <summary>
    /// Runtime control of one lane of one subscription (ADR-0012): `paused` — the consumers cancel that lane's consumer
    /// tag on their next poll (in-flight deliveries finish, expected set unchanged); `draining` — they keep consuming until
    /// the queue is empty and then set `paused` themselves; `active` — they consume again. The row is upserted and the
    /// command audited in one transaction. Unknown subscription/lane → ArgumentException.
    /// </summary>
    public async Task SetLaneStateAsync(string subscriptionId, string lane, string state, string actor, string reason, CancellationToken ct)
    {
        if (state is not (SubscriptionLane.Active or SubscriptionLane.Paused or SubscriptionLane.Draining))
        {
            throw new ArgumentException($"lane state {state} is not active|paused|draining", nameof(state));
        }
        if (!registrar.Registry.Subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            throw new ArgumentException($"subscription {subscriptionId} is not in topology v{registrar.Registry.TopologyVersion}", nameof(subscriptionId));
        }
        if (!subscription.Lanes.Contains(lane, StringComparer.Ordinal))
        {
            throw new ArgumentException($"subscription {subscriptionId} does not serve lane {lane}", nameof(lane));
        }
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("actor and reason are required");
        }
        if (actor.Length > MaxActor || reason.Length > MaxReason)
        {
            throw new ArgumentException($"actor ≤ {MaxActor} and reason ≤ {MaxReason} characters");
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? previous;
        await using (var select = new NpgsqlCommand("SELECT state FROM messaging.subscription_lanes WHERE subscription_id = @s AND lane = @l FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue("s", subscriptionId);
            select.Parameters.AddWithValue("l", lane);
            previous = (string?)await select.ExecuteScalarAsync(ct);
        }
        await Exec(conn, tx,
            """
            INSERT INTO messaging.subscription_lanes (subscription_id, lane, state, reason, actor, changed_at) VALUES (@s, @l, @state, @reason, @actor, now())
            ON CONFLICT (subscription_id, lane) DO UPDATE SET state = EXCLUDED.state, reason = EXCLUDED.reason, actor = EXCLUDED.actor, changed_at = now()
            """, ct, ("s", subscriptionId), ("l", lane), ("state", state), ("reason", reason), ("actor", actor));
        await AuditAsync(conn, tx, state switch { SubscriptionLane.Paused => "pause", SubscriptionLane.Draining => "drain", _ => "resume" }, subscriptionId, lane, actor, reason, new { from = previous ?? SubscriptionLane.Active, to = state }, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Subscription {Subscription} lane {Lane}: {From} → {To} by {Actor}: {Reason}", subscriptionId, lane, previous ?? SubscriptionLane.Active, state, actor, reason);
    }

    /// <summary>Operator-visible audit of every control command (P13); free-text actor, the same convention as the catalog audit (P12).</summary>
    public static async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string action, string? subscriptionId, string? lane, string actor, string reason, object? details, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO messaging.control_audit (action, subscription_id, lane, actor, reason, at, details) VALUES (@a, @s, @l, @actor, @reason, now(), @d)", conn, tx);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("s", (object?)subscriptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", (object?)lane ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actor", actor.Length > 128 ? actor[..128] : actor);
        cmd.Parameters.AddWithValue("reason", reason.Length > 1000 ? reason[..1000] : reason);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = details is null ? DBNull.Value : JsonSerializer.Serialize(details) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Audit of a command that has no subscription transaction of its own (scale): autocommit.</summary>
    public async Task AuditAsync(string action, string? subscriptionId, string? lane, string actor, string reason, object? details, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await AuditAsync(conn, null, action, subscriptionId, lane, actor, reason, details, ct);
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
