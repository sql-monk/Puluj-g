using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Puluj.Messaging;

/// <summary>
/// Turns dead-letters into durable quarantine (ADR-0004 W6a-2). Consumers quarantine before they nack, so most DLQ
/// messages already have their `processing.quarantine` row and are simply ACKed — including stale copies of an event
/// that an operator has since retried. The case this exists for is the broker's own dead-lettering after
/// `x-delivery-limit` crash loops (ADR-0001 §8): no receipt was ever committed, so it is written here with reason
/// `delivery_limit`. `processing.quarantine` is the source of truth; the DLQ is only the operational copy.
/// </summary>
public sealed class DlqConsumer(
    IReadOnlyList<string> subscriptionIds,
    BrokerConnection broker,
    TopologyRegistrar registrar,
    IDbContextFactory<PulujDbContext> factory,
    MessagingMetrics metrics,
    ILogger<DlqConsumer> logger) : BackgroundService
{
    private readonly List<IChannel> _channels = [];
    private long _handled;

    public long Handled => Volatile.Read(ref _handled);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var registry = registrar.Registry;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await registrar.EnsureRegisteredAsync(ct);
                var connection = await broker.GetAsync(ct);
                foreach (var subscriptionId in subscriptionIds)
                {
                    var subscription = registry.Subscription(subscriptionId);
                    if (!registry.Policy(subscription).Dlq)
                    {
                        continue;
                    }
                    foreach (var lane in subscription.Lanes)
                    {
                        var dlq = registry.DlqName(subscriptionId, lane);
                        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                        await channel.BasicQosAsync(0, 10, false, ct);
                        var consumer = new AsyncEventingBasicConsumer(channel);
                        consumer.ReceivedAsync += (_, ea) => OnReceivedAsync(channel, subscriptionId, lane, ea, ct);
                        await channel.BasicConsumeAsync(dlq, autoAck: false, consumerTag: $"dlq:{subscriptionId}:{lane}@{Environment.MachineName.ToLowerInvariant()}", consumer, cancellationToken: ct);
                        _channels.Add(channel);
                    }
                }
                logger.LogInformation("DLQ consumer watching {Subscriptions}", string.Join(",", subscriptionIds));
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DLQ consumer failed to start; retrying in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        foreach (var channel in _channels)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing DLQ channel");
            }
        }
    }

    private async Task OnReceivedAsync(IChannel channel, string subscriptionId, string lane, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var body = ea.Body.ToArray();
        var envelope = Envelope.TryParse(body, out var parseError);
        var eventId = envelope?.EventId ?? (Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : SubscriptionConsumer.BodyEventId(body));
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            // Already recorded (consumer quarantined before the nack, an earlier DLQ pass, or a resolved retry), or already
            // completed by another replica (stale copy of a republished event): nothing to add, just ACK.
            await using (var exists = new NpgsqlCommand(
                """
                SELECT 1 FROM processing.quarantine WHERE subscription_id = @s AND event_id = @e
                UNION ALL
                SELECT 1 FROM messaging.inbox WHERE subscription_id = @s AND event_id = @e AND outcome IN ('completed', 'noop')
                LIMIT 1
                """, conn, tx))
            {
                exists.Parameters.AddWithValue("s", subscriptionId);
                exists.Parameters.AddWithValue("e", eventId);
                if (await exists.ExecuteScalarAsync(ct) is not null)
                {
                    await tx.RollbackAsync(ct);
                    await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                    Interlocked.Increment(ref _handled);
                    return;
                }
            }
            var reason = envelope is null ? "invalid_payload" : "delivery_limit";
            await using (var inbox = new NpgsqlCommand(
                """
                INSERT INTO messaging.inbox (subscription_id, event_id, received_at, completed_at, outcome) VALUES (@s, @e, now(), now(), 'quarantined')
                ON CONFLICT (subscription_id, event_id) DO UPDATE SET outcome = 'quarantined', completed_at = now()
                """, conn, tx))
            {
                inbox.Parameters.AddWithValue("s", subscriptionId);
                inbox.Parameters.AddWithValue("e", eventId);
                await inbox.ExecuteNonQueryAsync(ct);
            }
            await SubscriptionConsumer.WriteReceiptAsync(conn, tx, subscriptionId, eventId, envelope?.TopologyVersion ?? registrar.Registry.TopologyVersion, "quarantined", reason, null, null, ct);
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO processing.quarantine (subscription_id, event_id, lane, reason, error, envelope, headers, quarantined_at)
                VALUES (@s, @e, @lane, @reason, @error, @envelope, @headers, now())
                ON CONFLICT (subscription_id, event_id) WHERE resolved_at IS NULL DO NOTHING
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("s", subscriptionId);
                insert.Parameters.AddWithValue("e", eventId);
                insert.Parameters.AddWithValue("lane", lane);
                insert.Parameters.AddWithValue("reason", reason);
                insert.Parameters.AddWithValue("error", (object?)(parseError ?? DeathReason(ea)) ?? DBNull.Value);
                insert.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = envelope?.ToArchiveJson() ?? SubscriptionConsumer.RawBodyAsJson(body) });
                insert.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = (object?)SubscriptionConsumer.HeadersAsJson(ea) ?? DBNull.Value });
                await insert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            metrics.Quarantined(subscriptionId, reason);
            logger.LogError("Dead-letter {EventId} for {Subscription}/{Lane} without a receipt: quarantined ({Reason})", eventId, subscriptionId, lane, reason);
            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
            Interlocked.Increment(ref _handled);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DLQ message {EventId} for {Subscription} could not be recorded; requeue", eventId, subscriptionId);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
        }
    }

    /// <summary>`x-death` of the broker (count, reason, original queue) when present.</summary>
    private static string? DeathReason(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers is { } headers && headers.TryGetValue("x-death", out var death) && death is List<object?> list && list.Count > 0 && list[0] is IDictionary<string, object?> first)
        {
            var reason = first.TryGetValue("reason", out var r) && r is byte[] rb ? System.Text.Encoding.UTF8.GetString(rb) : "?";
            var count = first.TryGetValue("count", out var c) ? c?.ToString() : "?";
            return $"x-death reason={reason} count={count}";
        }
        return null;
    }
}
