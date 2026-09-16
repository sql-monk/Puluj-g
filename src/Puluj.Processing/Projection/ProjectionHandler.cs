using System.Text.Json.Nodes;
using Npgsql;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging;

namespace Puluj.Processing.Projection;

/// <summary>
/// The `projection` subscription (plan §8.6, ADR-0011, P11): the map push adapter's durable half. One logical role with
/// competing consumers turns `incident.changed` into a Postgres NOTIFY `IncidentChanged{id, revision}` after the receipt
/// commits; every API replica LISTENs and pushes the DTO to its own SignalR clients — NOTIFY is the backplane, the
/// database stays the source of truth (the payload carries ids only). `track.changed`/`alert.changed` are a receipt-only
/// `noop`: their NOTIFY already comes from the writers after their commit (ADR-0009). Idempotent by construction — a
/// redelivery repeats a NOTIFY at worst, and the client ignores a revision it already has.
/// </summary>
public sealed class ProjectionHandler(INotifyPublisher notifier, PulujMetrics metrics) : IDeliveryHandler
{
    public const string Subscription = "projection";
    public const string IncidentChanged = "incident.changed";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Change(long IncidentId, int Revision, string ChangeKind, Guid? GenerationId);

    public Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        if (envelope.EventType != IncidentChanged)
        {
            return Task.FromResult<object?>(null);
        }
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without payload");
        var id = payload["incident_id"]?.GetValue<long>() ?? throw new PermanentDeliveryException("invalid_payload", "incident.changed without incident_id");
        var revision = payload["revision"]?.GetValue<int>() ?? throw new PermanentDeliveryException("invalid_payload", "incident.changed without revision");
        var change = payload["change"]?.GetValue<string>() ?? "updated";
        var generation = Guid.TryParse(payload["generation_id"]?.GetValue<string>(), out var g) ? g : (Guid?)null;
        return Task.FromResult<object?>(new Change(id, revision, change, generation));
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        if (state is not Change change)
        {
            metrics.WriterOutcome(Subscription, "noop_writer_notifies");
            return DeliveryResult.Noop("writer_notifies: track/alert NOTIFY comes from the owner after its commit");
        }
        if (envelope.Lane == "replay" || !await ActiveGenerationAsync(conn, tx, change.GenerationId, ct))
        {
            metrics.WriterOutcome(Subscription, "noop_not_live");
            return DeliveryResult.Noop("not_live: a replay lane or an inactive generation never reaches the live map (§11.4)");
        }
        metrics.WriterOutcome(Subscription, "notified");
        var evt = new PulujEvent(PulujEventType.IncidentChanged, change.IncidentId, envelope.OccurredAt, change.Revision);
        return new DeliveryResult("completed", change.ChangeKind) { AfterCommit = token => notifier.PublishAsync(evt, token) };
    }

    private static async Task<bool> ActiveGenerationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? generation, CancellationToken ct)
    {
        if (generation is null)
        {
            return true; // an event without a generation (older payloads) is live by definition
        }
        await using var cmd = new NpgsqlCommand("SELECT is_active FROM processing.generations WHERE generation_id = @g", conn, tx);
        cmd.Parameters.AddWithValue("g", generation.Value);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
