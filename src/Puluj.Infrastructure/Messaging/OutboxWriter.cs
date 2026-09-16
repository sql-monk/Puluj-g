using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Messaging.Topology;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Writes an event into `messaging.outbox` plus its expected `processing.deliveries` rows on the caller's open
/// transaction (ADR-0004 §2: business result and outbox commit together, or not at all). The relay publishes later.
/// The expected set is computed here, at commit time, from the registry (required subscriptions of the event type that
/// serve the lane and are `active`/`paused` in the database): that fixes "who has to complete this" by topology version
/// independent of which consumers are running (ADR-0002).
/// </summary>
public sealed class OutboxWriter(TopologyRegistrar registrar, ProcessingRuns runs, IOptions<MessagingOptions> options, TimeProvider clock)
{
    public TopologyRegistry Registry => registrar.Registry;
    public ProcessingRuns Runs => runs;
    public bool Enabled => options.Value.Outbox.Enabled;

    public sealed record OutboxWrite(long OutboxId, Guid EventId, IReadOnlyList<string> ExpectedSubscriptions);

    /// <summary>Fills identity/version fields the producer does not know (event id, published_at, topology version) and inserts.</summary>
    public async Task<OutboxWrite> EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, CancellationToken ct)
    {
        await registrar.EnsureRegisteredAsync(conn, tx, ct);
        var registry = registrar.Registry;
        var definition = registry.Event(envelope.EventType);
        if (envelope.EventId == Guid.Empty)
        {
            envelope.EventId = Guid.CreateVersion7();
        }
        if (envelope.PublishedAt == default)
        {
            envelope.PublishedAt = clock.GetUtcNow();
        }
        if (envelope.CausationId is null && envelope.EventType != "ingress.received")
        {
            envelope.CausationId = envelope.EventId; // bridge root (ADR-0003): no ingress event exists before P04
        }
        envelope.TopologyVersion = registry.TopologyVersion;
        var routingKey = registry.RoutingKey(envelope.Lane, envelope.EventType);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO messaging.outbox (event_id, event_type, lane, routing_key, target_queue, replay_source, envelope, created_at, attempts, next_attempt_at)
            VALUES (@event_id, @event_type, @lane, @routing_key, NULL, @replay_source, @envelope, now(), 0, now())
            RETURNING outbox_id
            """, conn, tx);
        insert.Parameters.AddWithValue("event_id", envelope.EventId);
        insert.Parameters.AddWithValue("event_type", envelope.EventType);
        insert.Parameters.AddWithValue("lane", envelope.Lane);
        insert.Parameters.AddWithValue("routing_key", routingKey);
        insert.Parameters.AddWithValue("replay_source", definition.ReplaySource);
        insert.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = envelope.ToJson() });
        var outboxId = (long)(await insert.ExecuteScalarAsync(ct))!;

        var statuses = await TopologyRegistrar.StatusesAsync(conn, tx, registry.TopologyVersion, ct);
        var branches = envelope.Payload?["expected_branches"] is JsonArray eb ? eb.Select(b => b?.GetValue<string>()).Where(b => b is not null).Select(b => b!).ToList() : null;
        var expected = registry.ExpectedSubscriptions(envelope.EventType, envelope.Lane, statuses, null, branches);
        foreach (var subscription in expected)
        {
            await using var delivery = new NpgsqlCommand(
                """
                INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at)
                VALUES (@event_id, @subscription_id, @v, now())
                ON CONFLICT DO NOTHING
                """, conn, tx);
            delivery.Parameters.AddWithValue("event_id", envelope.EventId);
            delivery.Parameters.AddWithValue("subscription_id", subscription.Id);
            delivery.Parameters.AddWithValue("v", registry.TopologyVersion);
            await delivery.ExecuteNonQueryAsync(ct);
        }
        return new OutboxWrite(outboxId, envelope.EventId, expected.Select(s => s.Id).ToList());
    }

    /// <summary>
    /// Redelivery of an already published event straight into one subscription's queue (admin retry after quarantine,
    /// ADR-0004 W6b). Same event id, same envelope JSON; the relay publishes it through the default exchange.
    /// </summary>
    public async Task<long> EnqueueRedeliveryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, string eventType, string lane, string envelopeJson, string targetQueue, CancellationToken ct)
    {
        await registrar.EnsureRegisteredAsync(conn, tx, ct);
        var registry = registrar.Registry;
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO messaging.outbox (event_id, event_type, lane, routing_key, target_queue, replay_source, envelope, created_at, attempts, next_attempt_at)
            VALUES (@event_id, @event_type, @lane, @routing_key, @target_queue, false, @envelope, now(), 0, now())
            RETURNING outbox_id
            """, conn, tx);
        insert.Parameters.AddWithValue("event_id", eventId);
        insert.Parameters.AddWithValue("event_type", eventType);
        insert.Parameters.AddWithValue("lane", lane);
        insert.Parameters.AddWithValue("routing_key", registry.RoutingKey(lane, eventType));
        insert.Parameters.AddWithValue("target_queue", targetQueue);
        insert.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = envelopeJson });
        return (long)(await insert.ExecuteScalarAsync(ct))!;
    }
}
