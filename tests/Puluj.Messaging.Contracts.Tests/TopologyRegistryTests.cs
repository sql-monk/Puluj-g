using System.Text.Json.Nodes;

namespace Puluj.Messaging.Contracts.Tests;

/// <summary>T03–T09, T14: консистентність topology.json, completion-manifest.json та asyncapi.yaml (ADR-0002/0005).</summary>
[Collection(ContractCollection.Name)]
public sealed class TopologyRegistryTests(ContractFiles contracts)
{
    // Hard-coded очікування з плану P01 (review B4): відсутній тип або підписка — тест падає, а не «самопосилання проходить».
    private static readonly string[] ExpectedEventTypes =
    [
        "ingress.received", "raw.stored", "message.normalized", "parse.completed",
        "llm.requested", "llm.completed", "llm.failed",
        "observations.recorded", "message.analysis.completed",
        "track.changed", "alert.changed", "incident.changed",
        "track.expiry.requested", "alert.expiry.requested",
    ];

    private static readonly string[] ExpectedSubscriptions =
    [
        "raw-writer", "normalizer", "parser", "llm-worker", "finalizer",
        "track-worker", "alert-worker", "incident-worker", "projection", "message-analytics", "archive",
    ];

    private static readonly string[] ExpectedProducerRoles = ["collectors", "watchdog", "outbox-relay", "reconciliation", "replay"];

    private static readonly Dictionary<string, string[]> ExpectedRequired = new(StringComparer.Ordinal)
    {
        ["ingress.received"] = ["raw-writer", "archive"],
        ["raw.stored"] = ["normalizer", "message-analytics", "archive"],
        ["message.normalized"] = ["parser"],
        ["parse.completed"] = ["finalizer"],
        ["llm.requested"] = ["llm-worker"],
        ["llm.completed"] = ["finalizer"],
        ["llm.failed"] = ["finalizer"],
        ["observations.recorded"] = ["archive"],
        ["message.analysis.completed"] = ["message-analytics", "archive"],
        ["track.changed"] = ["archive", "projection", "message-analytics"], // v6 (P09): archive keeps the events routable until projection exists
        ["alert.changed"] = ["archive", "projection", "message-analytics"],
        ["incident.changed"] = ["archive", "projection", "message-analytics"], // v7 (P10)
        ["track.expiry.requested"] = ["track-worker"],
        ["alert.expiry.requested"] = ["alert-worker"],
    };

    [Fact]
    public void T03_RegistryContainsExpectedEventTypesSubscriptionsAndRequiredSets()
    {
        Assert.Equal(10, contracts.Topology["topology_version"]!.GetValue<int>()); // v2 archive; v3 raw-writer; v4 normalizer/parser; v5 finalizer/llm-worker; v6 track/alert-worker; v7 incident-worker; v8 projection; v9 incident-worker replay lane (P14); v10 message-analytics active (P15)
        Assert.Equal(ExpectedEventTypes.Order(StringComparer.Ordinal), contracts.Events.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedSubscriptions.Order(StringComparer.Ordinal), contracts.Subscriptions.Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedProducerRoles.Order(StringComparer.Ordinal), contracts.ProducerRoles.Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var (eventType, required) in ExpectedRequired)
        {
            var actual = ContractFiles.Strings(contracts.Topology["events"]![eventType]!["required_subscriptions"]).Order(StringComparer.Ordinal);
            Assert.Equal(required.Order(StringComparer.Ordinal), actual);
        }
        // Envelope enum event_type == registry.
        var envelopeEnum = ContractFiles.Strings(contracts.LoadObject(Path.Combine("schemas", "envelope.schema.json"))["$defs"]!["eventType"]!["enum"]);
        Assert.Equal(ExpectedEventTypes.Order(StringComparer.Ordinal), envelopeEnum.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void T04_AllBindingsAndEmitsReferenceKnownEvents_EveryEventHasProducerAndSchema()
    {
        var events = contracts.Events.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var producers = contracts.Subscriptions.Select(s => s.Key).Concat(contracts.ProducerRoles.Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);

        foreach (var (eventType, definition) in contracts.Events)
        {
            var producer = definition!["producer"]!.GetValue<string>();
            Assert.True(producers.Contains(producer), $"{eventType}: unknown producer {producer}");
            var schema = Path.Combine(contracts.Root, definition["schema"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(schema), $"{eventType}: schema file missing {schema}");
            Assert.Matches("^[0-9]+\\.[0-9]+$", definition["schema_version"]!.GetValue<string>());
            // Producer справді оголошує цю подію у своїх emits.
            var producerNode = contracts.Topology["subscriptions"]![producer] ?? contracts.Topology["producer_roles"]![producer];
            Assert.Contains(eventType, ContractFiles.Strings(producerNode!["emits"]));
            foreach (var sub in ContractFiles.Strings(definition["required_subscriptions"]).Concat(ContractFiles.Strings(definition["optional_subscriptions"])))
            {
                Assert.True(contracts.Topology["subscriptions"]![sub] is not null, $"{eventType}: unknown subscription {sub}");
                Assert.Contains(eventType, ContractFiles.Strings(contracts.Topology["subscriptions"]![sub]!["bindings"]));
            }
        }

        var lanes = ContractFiles.Strings(contracts.Topology["lanes"]).ToHashSet(StringComparer.Ordinal);
        foreach (var (subscriptionId, subscription) in contracts.Subscriptions)
        {
            foreach (var binding in ContractFiles.Strings(subscription!["bindings"]))
            {
                Assert.True(events.Contains(binding), $"{subscriptionId}: binding to unknown event {binding}");
                var definition = contracts.Topology["events"]![binding]!;
                var declared = ContractFiles.Strings(definition["required_subscriptions"])
                    .Concat(ContractFiles.Strings(definition["optional_subscriptions"]))
                    .Concat(ContractFiles.Strings(definition["conditional_subscriptions"]?["by_manifest"]));
                Assert.True(declared.Contains(subscriptionId), $"{subscriptionId} binds {binding} but the event does not list it");
            }
            foreach (var emitted in ContractFiles.Strings(subscription["emits"]))
            {
                Assert.True(events.Contains(emitted), $"{subscriptionId}: emits unknown event {emitted}");
                Assert.Equal(subscriptionId, contracts.Topology["events"]![emitted]!["producer"]!.GetValue<string>());
            }
            Assert.All(ContractFiles.Strings(subscription["lanes"]), lane => Assert.Contains(lane, lanes));
            Assert.Matches("^P[0-9]{2}$", subscription["owner_task"]!.GetValue<string>());
            // P03 archive, P04 raw-writer, P05 normalizer/parser — active (runtime реалізовано); finalizer/llm-worker — paused
            // (черги для parse.completed/llm.requested існують з P05; active з P06); решта — planned до своїх задач.
            var expectedStatus = subscriptionId switch
            {
                "archive" or "raw-writer" or "normalizer" or "parser" or "finalizer" or "llm-worker" or "track-worker" or "alert-worker" or "incident-worker" or "projection" or "message-analytics" => "active",
                _ => "planned",
            };
            Assert.Equal(expectedStatus, subscription["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public void T05_CommandsHaveExactlyOneOwner_EventsHaveRequiredSubscribers()
    {
        foreach (var (eventType, definition) in contracts.Events)
        {
            var required = ContractFiles.Strings(definition!["required_subscriptions"]).ToList();
            switch (definition["kind"]!.GetValue<string>())
            {
                case "command":
                    var owner = definition["owner"]!.GetValue<string>();
                    Assert.Equal([owner], required);
                    Assert.Empty(ContractFiles.Strings(definition["optional_subscriptions"]));
                    Assert.Null(definition["conditional_subscriptions"]);
                    break;
                case "event":
                    Assert.True(required.Count >= 1, $"{eventType}: event without a required subscription");
                    Assert.Null(definition["owner"]);
                    break;
                default:
                    Assert.Fail($"{eventType}: unknown kind");
                    break;
            }
        }
    }

    [Fact]
    public void T06_ArchiveIsRequiredForEveryReplaySourceEvent()
    {
        var replaySources = contracts.Events.Where(e => e.Value!["replay_source"]!.GetValue<bool>()).Select(e => e.Key).ToList();
        Assert.Equal(["ingress.received", "message.analysis.completed", "observations.recorded", "raw.stored"], replaySources.Order(StringComparer.Ordinal));
        foreach (var eventType in replaySources)
        {
            Assert.Contains("archive", ContractFiles.Strings(contracts.Topology["events"]![eventType]!["required_subscriptions"]));
        }
        var archive = contracts.Topology["subscriptions"]!["archive"]!;
        Assert.True(archive["required"]!.GetValue<bool>());
        // Every replay source is archived; since v6 the archive also takes the aggregate change events (P09: the only active subscriber until projection).
        var bindings = ContractFiles.Strings(archive["bindings"]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(replaySources.Concat(["alert.changed", "incident.changed", "track.changed"]).Order(StringComparer.Ordinal), bindings);
    }

    [Fact]
    public void T07_WorkflowGraphIsAcyclic_AuditConsumersEmitNothing()
    {
        // Ребро: subscription → (emits event) → subscriptions, що bind цю подію (required/optional/conditional).
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (subscriptionId, subscription) in contracts.Subscriptions)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var emitted in ContractFiles.Strings(subscription!["emits"]))
            {
                var definition = contracts.Topology["events"]![emitted]!;
                foreach (var target in ContractFiles.Strings(definition["required_subscriptions"])
                             .Concat(ContractFiles.Strings(definition["optional_subscriptions"]))
                             .Concat(ContractFiles.Strings(definition["conditional_subscriptions"]?["by_manifest"])))
                {
                    next.Add(target);
                }
            }
            graph[subscriptionId] = next;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 = new, 1 = visiting, 2 = done
        foreach (var start in graph.Keys)
        {
            Visit(start, []);
        }

        void Visit(string node, List<string> path)
        {
            if (state.TryGetValue(node, out var s))
            {
                if (s == 1)
                {
                    Assert.Fail("publication cycle: " + string.Join(" → ", path.Append(node)));
                }
                return;
            }
            state[node] = 1;
            path.Add(node);
            foreach (var next in graph[node])
            {
                Visit(next, path);
            }
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        // §6.3: analytics/audit/projection не публікують lifecycle подій про lifecycle події.
        var mustBeSilent = ContractFiles.Strings(contracts.Manifest["no_lifecycle_of_lifecycle"]!["subscriptions_with_empty_emits"]).ToList();
        Assert.Equal(["archive", "message-analytics", "projection"], mustBeSilent.Order(StringComparer.Ordinal));
        foreach (var subscriptionId in mustBeSilent)
        {
            Assert.Empty(ContractFiles.Strings(contracts.Topology["subscriptions"]![subscriptionId]!["emits"]));
        }
        // Producer-only ролі relay/reconciliation теж нічого не публікують самі.
        Assert.Empty(ContractFiles.Strings(contracts.Topology["producer_roles"]!["outbox-relay"]!["emits"]));
        Assert.Empty(ContractFiles.Strings(contracts.Topology["producer_roles"]!["reconciliation"]!["emits"]));
    }

    [Fact]
    public void T08_RequiredSubscriptionsHaveNoTtlNoDropOldestAndDlq()
    {
        var policies = contracts.Topology["queue_policies"]!;
        var required = policies["required"]!;
        Assert.Equal("none", required["ttl"]!.GetValue<string>());
        Assert.False(required["drop_oldest"]!.GetValue<bool>());
        Assert.True(required["dlq"]!.GetValue<bool>());
        Assert.True(required["durable"]!.GetValue<bool>());
        Assert.True(required["max_delivery_attempts"]!.GetValue<int>() >= 1);
        Assert.False(policies["optional"]!["drop_oldest"]!.GetValue<bool>());

        foreach (var (subscriptionId, subscription) in contracts.Subscriptions)
        {
            var policy = subscription!["queue_policy"]!.GetValue<string>();
            Assert.True(policies[policy] is not null, $"{subscriptionId}: unknown queue policy {policy}");
            if (subscription["required"]!.GetValue<bool>())
            {
                Assert.Equal("required", policy);
            }
            Assert.False(string.IsNullOrWhiteSpace(subscription["idempotency"]?.GetValue<string>()), $"{subscriptionId}: idempotency strategy missing");
        }
        Assert.Equal("topic", contracts.Topology["exchange"]!["type"]!.GetValue<string>());
        Assert.True(contracts.Topology["exchange"]!["durable"]!.GetValue<bool>());
        Assert.Contains("{lane}", contracts.Topology["routing_key_pattern"]!.GetValue<string>());
        Assert.Contains("{subscription_id}", contracts.Topology["queue_name_pattern"]!.GetValue<string>());
        Assert.EndsWith(".dlq", contracts.Topology["dlq_name_pattern"]!.GetValue<string>());
    }

    [Fact]
    public void T09_CompletionManifestCoversEveryAnalysisOutcomeAndKnownBranches()
    {
        var outcomes = contracts.Manifest["analysis_outcomes"]!.AsObject();
        var schemaOutcomes = ContractFiles.Strings(contracts.LoadObject(Path.Combine("schemas", "common.schema.json"))["$defs"]!["analysisOutcome"]!["enum"]).ToList();
        Assert.Equal(["completed", "failed", "needs_review", "no_facts", "unsupported"], schemaOutcomes.Order(StringComparer.Ordinal));
        Assert.Equal(schemaOutcomes.Order(StringComparer.Ordinal), outcomes.Select(o => o.Key).Where(k => !k.StartsWith('$')).Order(StringComparer.Ordinal));

        var required = contracts.Subscriptions.Where(s => s.Value!["required"]!.GetValue<bool>()).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (outcome, rule) in outcomes)
        {
            if (outcome.StartsWith('$'))
            {
                continue;
            }
            Assert.True(rule!["terminal"]!.GetValue<bool>(), $"{outcome} must be terminal");
            var branches = ContractFiles.Strings(rule["expected_branches"]).ToList();
            // message.analysis.completed обов'язковий для всіх outcomes → його required subscriptions очікувані завжди.
            Assert.Contains("message-analytics", branches);
            Assert.Contains("archive", branches);
            Assert.All(branches, b => Assert.Contains(b, required));
            var domain = rule["domain_branches"]!.GetValue<string>();
            Assert.Equal(outcome == "completed" ? "by_observation_category" : "none", domain);
            Assert.Equal(outcome == "completed", rule["publishes_observations"]!.GetValue<bool>());
        }
        Assert.Equal("needs_attention", outcomes["failed"]!["workflow_status"]!.GetValue<string>());
        Assert.Equal("needs_attention", outcomes["needs_review"]!["workflow_status"]!.GetValue<string>());

        // Категорія observation → доменна гілка; кожна гілка — required subscription, що bind observations.recorded.
        var byCategory = contracts.Manifest["domain_branches_by_observation_category"]!.AsObject();
        var categories = ContractFiles.Strings(contracts.LoadObject(Path.Combine("schemas", "common.schema.json"))["$defs"]!["observationCategory"]!["enum"]).ToList();
        Assert.Equal(categories.Order(StringComparer.Ordinal), byCategory.Where(c => !c.Key.StartsWith('$')).Select(c => c.Key).Order(StringComparer.Ordinal));
        var conditional = ContractFiles.Strings(contracts.Topology["events"]!["observations.recorded"]!["conditional_subscriptions"]!["by_manifest"]).ToHashSet(StringComparer.Ordinal);
        foreach (var (category, branch) in byCategory)
        {
            if (category.StartsWith('$'))
            {
                continue;
            }
            if (branch is null)
            {
                Assert.Equal("info", category);
                continue;
            }
            var subscriptionId = branch.GetValue<string>();
            Assert.Contains(subscriptionId, required);
            Assert.Contains(subscriptionId, conditional);
            Assert.Contains("observations.recorded", ContractFiles.Strings(contracts.Topology["subscriptions"]![subscriptionId]!["bindings"]));
        }

        Assert.Equal(["completed", "noop", "quarantined", "waived"], ContractFiles.Strings(contracts.Manifest["delivery_terminal_outcomes"]));
        var semantics = contracts.Manifest["delivery_terminal_outcome_semantics"]!.AsObject();
        Assert.Equal(4, semantics.Count);
        Assert.Contains("НЕ успіх", semantics["quarantined"]!.GetValue<string>());

        // Fixture expected_branches узгоджені з manifest за категоріями observations.
        var byEventId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in contracts.Files(Path.Combine("fixtures", "valid")))
        {
            var envelope = contracts.LoadObject(Path.Combine("fixtures", "valid", Path.GetFileName(file)));
            byEventId[envelope["event_id"]!.GetValue<string>()] = envelope;
            if (envelope["event_type"]!.GetValue<string>() != "observations.recorded")
            {
                continue;
            }
            var recorded = envelope["payload"]!;
            var expected = recorded["observations"]!.AsArray()
                .Select(o => byCategory[o!["category"]!.GetValue<string>()]?.GetValue<string>())
                .Where(b => b is not null).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(expected, ContractFiles.Strings(recorded["expected_branches"]).Order(StringComparer.Ordinal));
        }

        // Causal chain: producer кожного *.changed fixture — очікувана гілка події-causation (review B5).
        foreach (var envelope in byEventId.Values.Where(e => e["event_type"]!.GetValue<string>().EndsWith(".changed", StringComparison.Ordinal)))
        {
            var cause = byEventId[envelope["causation_id"]!.GetValue<string>()];
            Assert.Equal("observations.recorded", cause["event_type"]!.GetValue<string>());
            Assert.Equal(cause["correlation_id"]!.GetValue<string>(), envelope["correlation_id"]!.GetValue<string>());
            var producer = envelope["producer"]!.GetValue<string>();
            Assert.Contains(producer, ContractFiles.Strings(cause["payload"]!["expected_branches"]));
            var observationIds = cause["payload"]!["observations"]!.AsArray().Select(o => o!["observation_id"]!.GetValue<string>()).ToList();
            Assert.All(ContractFiles.Strings(envelope["payload"]!["observation_ids"]), id => Assert.Contains(id, observationIds));
        }
    }

    [Fact]
    public void T14_AsyncApiReferencesExistingSchemasAndEveryEventType()
    {
        var yaml = File.ReadAllText(Path.Combine(contracts.Root, "asyncapi.yaml"));
        Assert.StartsWith("#", yaml, StringComparison.Ordinal);
        Assert.Contains("asyncapi: 3.0.0", yaml);

        var refs = System.Text.RegularExpressions.Regex.Matches(yaml, @"\$ref: '(schemas/[^']+)'").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("schemas/envelope.schema.json", refs);
        foreach (var r in refs)
        {
            Assert.True(File.Exists(Path.Combine(contracts.Root, r.Replace('/', Path.DirectorySeparatorChar))), $"asyncapi.yaml references missing {r}");
        }
        foreach (var (eventType, definition) in contracts.Events)
        {
            Assert.Contains($"address: puluj.{{lane}}.{eventType}", yaml);
            Assert.Contains($"x-event-type: {eventType}", yaml);
            Assert.Contains($"$ref: '{definition!["schema"]!.GetValue<string>()}'", yaml);
        }
        foreach (var (subscriptionId, subscription) in contracts.Subscriptions)
        {
            foreach (var binding in ContractFiles.Strings(subscription!["bindings"]))
            {
                Assert.Contains($"receive_{subscriptionId.Replace('-', '_')}_{binding.Replace('.', '_')}:", yaml);
            }
        }
        // Внутрішні $ref (#/channels/x, #/components/messages/x) вказують на визначені channel ids.
        var channelIds = System.Text.RegularExpressions.Regex.Matches(yaml, @"^  ([a-z_]+):\n    address:", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(contracts.Events.Count(), channelIds.Count);
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(yaml, @"\$ref: '#/channels/([a-z_]+)").Select(m => m.Groups[1].Value))
        {
            Assert.Contains(m, channelIds);
        }
    }
}
