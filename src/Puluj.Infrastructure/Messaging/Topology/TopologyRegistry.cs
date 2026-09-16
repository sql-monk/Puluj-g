using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puluj.Infrastructure.Messaging.Topology;

/// <summary>
/// In-memory view of `contracts/messaging/topology.json` (ADR-0002), embedded into this assembly at build time so the
/// runtime and the contract tests read the very same file. Names (exchange, routing keys, queues, DLQs), event
/// definitions (producer, schema version, replay source, required subscriptions) and subscriptions (bindings, lanes,
/// queue policy with broker arguments, initial status). Nothing here talks to the broker or the database.
/// </summary>
public sealed class TopologyRegistry
{
    public const string ResourceName = "Puluj.Infrastructure.Messaging.topology.json";
    public const string DeadLetterExchange = "puluj.dlx";

    public int TopologyVersion { get; }
    /// <summary>SHA-256 of the file bytes, recorded in `messaging.topology_versions`: the same version number with different content is refused.</summary>
    public string Hash { get; }
    public string ExchangeName { get; }
    public IReadOnlyList<string> Lanes { get; }
    public IReadOnlyDictionary<string, EventDefinition> Events { get; }
    public IReadOnlyDictionary<string, SubscriptionDefinition> Subscriptions { get; }
    public IReadOnlyDictionary<string, QueuePolicy> QueuePolicies { get; }

    private readonly string _queuePattern;
    private readonly string _dlqPattern;
    private readonly string _routingPattern;

    /// <param name="ManifestSubscriptions">`conditional_subscriptions.by_manifest`: subscriptions expected only when the payload's `expected_branches` names them (P09).</param>
    public sealed record EventDefinition(string Type, string Kind, string Producer, string SchemaVersion, string Scope, bool ReplaySource, IReadOnlyList<string> RequiredSubscriptions, IReadOnlyList<string> OptionalSubscriptions,
        IReadOnlyList<string> ManifestSubscriptions)
    {
        public int SchemaMajor => ParseMajor(SchemaVersion) ?? throw new InvalidOperationException($"topology.json: event {Type} has invalid schema_version '{SchemaVersion}'");
    }

    public sealed record SubscriptionDefinition(string Id, bool Required, string Status, string? OwnerTask, IReadOnlyList<string> Bindings, IReadOnlyList<string> Lanes, IReadOnlyList<string> Emits, string QueuePolicy);

    public sealed record QueuePolicy(string Name, bool Durable, bool Dlq, int MaxDeliveryAttempts, IReadOnlyDictionary<string, object> BrokerArguments);

    public TopologyRegistry(JsonObject topology)
    {
        Hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(topology)));
        TopologyVersion = topology["topology_version"]!.GetValue<int>();
        ExchangeName = topology["exchange"]!["name"]!.GetValue<string>();
        _queuePattern = topology["queue_name_pattern"]!.GetValue<string>();
        _dlqPattern = topology["dlq_name_pattern"]!.GetValue<string>();
        _routingPattern = topology["routing_key_pattern"]!.GetValue<string>();
        Lanes = Strings(topology["lanes"]);

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
                    if (!key.StartsWith('$'))
                    {
                        args[key] = value!.GetValueKind() == JsonValueKind.Number ? value.GetValue<int>() : value.GetValue<string>();
                    }
                }
            }
            policies[name] = new QueuePolicy(name, node["durable"]!.GetValue<bool>(), node["dlq"]!.GetValue<bool>(), node["max_delivery_attempts"]!.GetValue<int>(), args);
        }
        QueuePolicies = policies;

        var events = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
        foreach (var (type, node) in topology["events"]!.AsObject())
        {
            if (type.StartsWith('$'))
            {
                continue;
            }
            events[type] = new EventDefinition(
                type,
                node!["kind"]!.GetValue<string>(),
                node["producer"]!.GetValue<string>(),
                node["schema_version"]!.GetValue<string>(),
                node["scope"]!.GetValue<string>(),
                node["replay_source"]?.GetValue<bool>() ?? false,
                Strings(node["required_subscriptions"]),
                Strings(node["optional_subscriptions"]),
                Strings(node["conditional_subscriptions"]?["by_manifest"]));
        }
        Events = events;

        var subscriptions = new Dictionary<string, SubscriptionDefinition>(StringComparer.Ordinal);
        foreach (var (id, node) in topology["subscriptions"]!.AsObject())
        {
            if (id.StartsWith('$'))
            {
                continue;
            }
            subscriptions[id] = new SubscriptionDefinition(
                id,
                node!["required"]!.GetValue<bool>(),
                node["status"]!.GetValue<string>(),
                node["owner_task"]?.GetValue<string>(),
                Strings(node["bindings"]),
                Strings(node["lanes"]),
                Strings(node["emits"]),
                node["queue_policy"]!.GetValue<string>());
        }
        Subscriptions = subscriptions;
    }

    /// <summary>The embedded contract file (the build copies `contracts/messaging/topology.json`).</summary>
    public static TopologyRegistry LoadEmbedded()
    {
        using var stream = typeof(TopologyRegistry).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found; check Puluj.Infrastructure.csproj");
        return new TopologyRegistry(JsonNode.Parse(stream)!.AsObject());
    }

    public static TopologyRegistry Load(string path) => new(JsonNode.Parse(File.ReadAllText(path))!.AsObject());

    public string QueueName(string subscriptionId, string lane) => _queuePattern.Replace("{subscription_id}", subscriptionId).Replace("{lane}", lane);
    public string DlqName(string subscriptionId, string lane) => _dlqPattern.Replace("{subscription_id}", subscriptionId).Replace("{lane}", lane);
    public string RoutingKey(string lane, string eventType) => _routingPattern.Replace("{lane}", lane).Replace("{event_type}", eventType);

    public EventDefinition Event(string eventType) =>
        Events.TryGetValue(eventType, out var definition) ? definition : throw new KeyNotFoundException($"topology.json: unknown event type '{eventType}'");

    public SubscriptionDefinition Subscription(string subscriptionId) =>
        Subscriptions.TryGetValue(subscriptionId, out var definition) ? definition : throw new KeyNotFoundException($"topology.json: unknown subscription '{subscriptionId}'");

    public QueuePolicy Policy(SubscriptionDefinition subscription) => QueuePolicies[subscription.QueuePolicy];

    /// <summary>
    /// Subscriptions that must complete a delivery of <paramref name="eventType"/> on <paramref name="lane"/> (ADR-0002):
    /// required for the event, serving the lane, and in one of <paramref name="activeStatuses"/> (by default: `active` or
    /// `paused` — a stopped required consumer stays interested; a `planned` one is not, it gets a backfill later).
    /// <paramref name="statusOverride"/> supplies the database-owned status per subscription id when known.
    /// </summary>
    public IReadOnlyList<SubscriptionDefinition> ExpectedSubscriptions(string eventType, string lane, IReadOnlyDictionary<string, string>? statusOverride = null, IReadOnlySet<string>? activeStatuses = null, IEnumerable<string>? expectedBranches = null)
    {
        activeStatuses ??= DefaultExpectedStatuses;
        var definition = Event(eventType);
        var result = new List<SubscriptionDefinition>();
        // The completion manifest's conditional branches (`expected_branches` of observations.recorded, P09): expected like a required subscription when named.
        var ids = expectedBranches is null
            ? definition.RequiredSubscriptions
            : definition.RequiredSubscriptions.Concat(expectedBranches.Where(b => definition.ManifestSubscriptions.Contains(b, StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in ids)
        {
            var subscription = Subscription(id);
            var status = statusOverride is not null && statusOverride.TryGetValue(id, out var s) ? s : subscription.Status;
            if (subscription.Lanes.Contains(lane, StringComparer.Ordinal) && activeStatuses.Contains(status))
            {
                result.Add(subscription);
            }
        }
        return result;
    }

    public static readonly IReadOnlySet<string> DefaultExpectedStatuses = new HashSet<string>(StringComparer.Ordinal) { "active", "paused" };

    /// <summary>Queue arguments of a subscription queue: policy broker_arguments + DLX routing (ADR-0002 invariants: immutable after declare).</summary>
    public Dictionary<string, object?> QueueArguments(SubscriptionDefinition subscription, string lane)
    {
        var policy = Policy(subscription);
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

    /// <summary>MAJOR of a `MAJOR.MINOR` schema version; null when the string is not a valid version (→ quarantine, ADR-0003).</summary>
    public static int? ParseMajor(string? schemaVersion)
    {
        if (string.IsNullOrEmpty(schemaVersion))
        {
            return null;
        }
        var dot = schemaVersion.IndexOf('.');
        if (dot <= 0 || dot == schemaVersion.Length - 1)
        {
            return null;
        }
        return int.TryParse(schemaVersion.AsSpan(0, dot), out var major) && int.TryParse(schemaVersion.AsSpan(dot + 1), out _) ? major : null;
    }

    private static IReadOnlyList<string> Strings(JsonNode? array) =>
        array is null ? [] : array.AsArray().Select(n => n!.GetValue<string>()).ToList();
}
