using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Puluj.Messaging;

/// <summary>
/// Declares the broker topology of ADR-0002 from the registry: topic exchange, dead-letter exchange, and for every
/// subscription that is `active` or `paused` in the database one quorum queue per lane with its DLQ and bindings.
/// Idempotent: re-running it re-creates a dropped binding (the drift the spike's C08 showed passive declare cannot
/// see), so reconciliation calls it periodically. Queue arguments are immutable after the first declare: a changed
/// argument answers PRECONDITION_FAILED, which is reported as an alarm and never retried in a loop — such a change
/// means a new queue name plus transfer (ADR-0002 invariants).
/// </summary>
public sealed class TopologyDeclarer(
    TopologyRegistrar registrar,
    BrokerConnection broker,
    IDbContextFactory<PulujDbContext> factory,
    MessagingMetrics metrics,
    ILogger<TopologyDeclarer> logger)
{
    public TopologyRegistry Registry => registrar.Registry;

    public sealed record DeclareResult(IReadOnlyList<string> Queues, IReadOnlyList<string> Failed);

    /// <summary>Subscriptions whose queues exist: `active`/`paused` in `messaging.subscriptions` for the current version.</summary>
    public async Task<IReadOnlyList<TopologyRegistry.SubscriptionDefinition>> ServedSubscriptionsAsync(CancellationToken ct)
    {
        await registrar.EnsureRegisteredAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var statuses = await TopologyRegistrar.StatusesAsync(conn, null, Registry.TopologyVersion, ct);
        return Registry.Subscriptions.Values
            .Where(s => statuses.TryGetValue(s.Id, out var status) && TopologyRegistry.DefaultExpectedStatuses.Contains(status))
            .ToList();
    }

    public async Task<DeclareResult> DeclareAsync(CancellationToken ct)
    {
        var served = await ServedSubscriptionsAsync(ct);
        var connection = await broker.GetAsync(ct);
        var declared = new List<string>();
        var failed = new List<string>();
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: ct))
        {
            await channel.ExchangeDeclareAsync(Registry.ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
            await channel.ExchangeDeclareAsync(TopologyRegistry.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        }
        foreach (var subscription in served)
        {
            var policy = Registry.Policy(subscription);
            foreach (var lane in subscription.Lanes)
            {
                var queue = Registry.QueueName(subscription.Id, lane);
                // A failed declare closes the channel: one channel per queue keeps the others independent.
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                try
                {
                    if (policy.Dlq)
                    {
                        // DLX/DLQ before the main queue: at-least-once dead-lettering holds messages until the DLQ confirms.
                        var dlq = Registry.DlqName(subscription.Id, lane);
                        await channel.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false,
                            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
                        await channel.QueueBindAsync(dlq, TopologyRegistry.DeadLetterExchange, dlq, cancellationToken: ct);
                    }
                    await channel.QueueDeclareAsync(queue, durable: policy.Durable, exclusive: false, autoDelete: false,
                        arguments: Registry.QueueArguments(subscription, lane), cancellationToken: ct);
                    foreach (var eventType in subscription.Bindings)
                    {
                        await channel.QueueBindAsync(queue, Registry.ExchangeName, Registry.RoutingKey(lane, eventType), cancellationToken: ct);
                    }
                    declared.Add(queue);
                }
                catch (OperationInterruptedException ex)
                {
                    failed.Add(queue);
                    metrics.TopologyDeclareFailed(queue);
                    logger.LogError(ex, "Topology declare failed for {Queue} (reply {Code}): existing queue arguments differ from topology.json; migrate via a new queue name", queue, ex.ShutdownReason?.ReplyCode);
                }
            }
        }
        logger.LogInformation("Topology v{Version} declared: {Count} queues ({Subscriptions})", Registry.TopologyVersion, declared.Count, string.Join(",", served.Select(s => s.Id)));
        return new DeclareResult(declared, failed);
    }

    /// <summary>Readiness (ADR-0002): required queues of served subscriptions that do not exist. Bindings drift is repaired by <see cref="DeclareAsync"/>, not detected here.</summary>
    public async Task<IReadOnlyList<string>> MissingRequiredQueuesAsync(CancellationToken ct)
    {
        var served = await ServedSubscriptionsAsync(ct);
        var connection = await broker.GetAsync(ct);
        var missing = new List<string>();
        foreach (var subscription in served.Where(s => s.Required))
        {
            foreach (var lane in subscription.Lanes)
            {
                var queue = Registry.QueueName(subscription.Id, lane);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                try
                {
                    await channel.QueueDeclarePassiveAsync(queue, ct);
                }
                catch (OperationInterruptedException)
                {
                    missing.Add(queue);
                }
            }
        }
        return missing;
    }
}
