using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging;

/// <summary>
/// The `archive` subscription (ADR-0002: required for every replay-source event): appends the envelope to
/// `messaging.events`. Idempotent by primary key, so a republish of the same event id is a no-op insert and the
/// delivery still completes. It emits nothing (no lifecycle-of-lifecycle, plan §6.3).
/// </summary>
public sealed class ArchiveHandler : IDeliveryHandler
{
    public const string Subscription = "archive";

    public string SubscriptionId => Subscription;

    public Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct) => Task.FromResult<object?>(null);

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO messaging.events (event_id, event_type, lane, correlation_id, causation_id, raw_message_id, processing_run_id, topology_version, occurred_at, published_at, envelope, archived_at)
            VALUES (@event_id, @event_type, @lane, @correlation_id, @causation_id, @raw_message_id, @run_id, @topology_version, @occurred_at, @published_at, @envelope, now())
            ON CONFLICT (event_id) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("event_id", envelope.EventId);
        cmd.Parameters.AddWithValue("event_type", envelope.EventType);
        cmd.Parameters.AddWithValue("lane", envelope.Lane);
        cmd.Parameters.AddWithValue("correlation_id", envelope.CorrelationId);
        cmd.Parameters.AddWithValue("causation_id", (object?)envelope.CausationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("raw_message_id", (object?)envelope.RawMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("run_id", envelope.ProcessingRunId);
        cmd.Parameters.AddWithValue("topology_version", envelope.TopologyVersion);
        cmd.Parameters.AddWithValue("occurred_at", envelope.OccurredAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("published_at", envelope.PublishedAt.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = envelope.ToArchiveJson() });
        var inserted = await cmd.ExecuteNonQueryAsync(ct);
        return inserted == 0 ? DeliveryResult.Noop("already archived") : DeliveryResult.Completed;
    }
}
