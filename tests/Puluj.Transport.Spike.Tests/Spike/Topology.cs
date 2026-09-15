using System.Text.Json.Nodes;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>
/// Читає contracts/messaging/topology.json і оголошує topology у брокері (ADR-0002): topic exchange, quorum queue на
/// підписку × lane з DLX/DLQ, bindings за routing key puluj.{lane}.{event_type}. Spike-код: P03 переписує в runtime.
/// </summary>
public sealed class Topology
{
    public const string DeadLetterExchange = "puluj.dlx";

    public string ExchangeName { get; }
    public IReadOnlyList<string> Lanes { get; }
    public IReadOnlyDictionary<string, Subscription> Subscriptions { get; }
    public IReadOnlyDictionary<string, QueuePolicy> QueuePolicies { get; }
    public int TopologyVersion { get; }

    public sealed record Subscription(string Id, IReadOnlyList<string> Bindings, IReadOnlyList<string> Lanes, string QueuePolicy, bool Required);

    public sealed record QueuePolicy(bool Durable, bool Dlq, int MaxDeliveryAttempts, IReadOnlyDictionary<string, object> BrokerArguments);

    private readonly string _queuePattern;
    private readonly string _dlqPattern;
    private readonly string _routingPattern;

    public Topology(JsonObject topology)
    {
        TopologyVersion = topology["topology_version"]!.GetValue<int>();
        ExchangeName = topology["exchange"]!["name"]!.GetValue<string>();
        _queuePattern = topology["queue_name_pattern"]!.GetValue<string>();
        _dlqPattern = topology["dlq_name_pattern"]!.GetValue<string>();
        _routingPattern = topology["routing_key_pattern"]!.GetValue<string>();
        Lanes = topology["lanes"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();

        var policies = new Dictionary<string, QueuePolicy>(StringComparer.Ordinal);
        foreach (var (name, node) in topology["queue_policies"]!.AsObject())
        {
            if (name.StartsWith('$'))
            {
                continue;
            }
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            if (node!["broker_arguments"] is JsonObject brokerArgs)
            {
                foreach (var (key, value) in brokerArgs)
                {
                    if (key.StartsWith('$'))
                    {
                        continue;
                    }
                    args[key] = value!.GetValueKind() == System.Text.Json.JsonValueKind.Number ? value.GetValue<int>() : value.GetValue<string>();
                }
            }
            policies[name] = new QueuePolicy(node["durable"]!.GetValue<bool>(), node["dlq"]!.GetValue<bool>(), node["max_delivery_attempts"]!.GetValue<int>(), args);
        }
        QueuePolicies = policies;

        var subscriptions = new Dictionary<string, Subscription>(StringComparer.Ordinal);
        foreach (var (id, node) in topology["subscriptions"]!.AsObject())
        {
            subscriptions[id] = new Subscription(
                id,
                node!["bindings"]!.AsArray().Select(b => b!.GetValue<string>()).ToList(),
                node["lanes"]!.AsArray().Select(l => l!.GetValue<string>()).ToList(),
                node["queue_policy"]!.GetValue<string>(),
                node["required"]!.GetValue<bool>());
        }
        Subscriptions = subscriptions;
    }

    public static Topology Load(string path) => new(JsonNode.Parse(File.ReadAllText(path))!.AsObject());

    public string QueueName(string subscriptionId, string lane) => _queuePattern.Replace("{subscription_id}", subscriptionId).Replace("{lane}", lane);
    public string DlqName(string subscriptionId, string lane) => _dlqPattern.Replace("{subscription_id}", subscriptionId).Replace("{lane}", lane);
    public string RoutingKey(string lane, string eventType) => _routingPattern.Replace("{lane}", lane).Replace("{event_type}", eventType);

    public IEnumerable<(string Subscription, string Lane, string Queue)> Queues() =>
        Subscriptions.Values.SelectMany(s => s.Lanes.Select(l => (s.Id, l, QueueName(s.Id, l))));

    /// <summary>Аргументи черги підписки: policy broker_arguments + DLX/DLQ routing. Idempotent для повторного declare.</summary>
    public Dictionary<string, object?> QueueArguments(Subscription subscription, string lane)
    {
        var policy = QueuePolicies[subscription.QueuePolicy];
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in policy.BrokerArguments)
        {
            args[key] = value;
        }
        if (policy.Dlq)
        {
            args["x-dead-letter-exchange"] = DeadLetterExchange;
            args["x-dead-letter-routing-key"] = DlqName(subscription.Id, lane);
        }
        return args;
    }

    /// <summary>Заздалегідь створює всю topology (без auto-delete/exclusive). Повторний виклик — no-op.</summary>
    public async Task DeclareAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        foreach (var subscription in Subscriptions.Values)
        {
            var policy = QueuePolicies[subscription.QueuePolicy];
            foreach (var lane in subscription.Lanes)
            {
                var queue = QueueName(subscription.Id, lane);
                if (policy.Dlq)
                {
                    var dlq = DlqName(subscription.Id, lane);
                    await channel.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false,
                        arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
                    await channel.QueueBindAsync(dlq, DeadLetterExchange, dlq, cancellationToken: ct);
                }
                await channel.QueueDeclareAsync(queue, durable: policy.Durable, exclusive: false, autoDelete: false,
                    arguments: QueueArguments(subscription, lane), cancellationToken: ct);
                foreach (var eventType in subscription.Bindings)
                {
                    await channel.QueueBindAsync(queue, ExchangeName, RoutingKey(lane, eventType), cancellationToken: ct);
                }
            }
        }
    }

    /// <summary>Readiness (ADR-0002): усі required черги існують. Passive declare закриває канал при відсутності, тому по каналу на чергу.</summary>
    public async Task<IReadOnlyList<string>> MissingRequiredQueuesAsync(IConnection connection, CancellationToken ct = default)
    {
        var missing = new List<string>();
        foreach (var (subscriptionId, lane, queue) in Queues())
        {
            if (!Subscriptions[subscriptionId].Required)
            {
                continue;
            }
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
        return missing;
    }
}
