using Puluj.Transport.Spike.Tests.Spike;
using RabbitMQ.Client;

namespace Puluj.Transport.Spike.Tests;

/// <summary>Gate хвилі 1 (plan §12) на реальному RabbitMQ: topology, fan-out, competing consumers, lanes, DLQ.</summary>
[Collection(GateCollection.Name)]
public sealed class GateTests(RabbitMqFixture broker)
{
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(10);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.True(condition(), "condition not reached within timeout");
    }

    private async Task PurgeAsync(IConnection connection, params string[] queues)
    {
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    [Fact]
    public async Task G00_TopologyDeclaredFromRegistry_AllRequiredQueuesQuorumWithBindingsAndDlq()
    {
        var topology = broker.Topology;
        await using var connection = await broker.ConnectAsync();
        Assert.Empty(await topology.MissingRequiredQueuesAsync(connection));

        var expectedQueues = topology.Queues().Count();
        Assert.True(expectedQueues >= 30, $"expected ≥30 queues, got {expectedQueues}");
        foreach (var (subscriptionId, lane, queue) in topology.Queues())
        {
            var stats = await broker.QueueAsync(queue);
            Assert.Equal("quorum", stats.Type);
            var bindings = await broker.BindingsAsync(queue);
            var routingKeys = bindings.Where(b => b!["source"]!.GetValue<string>() == topology.ExchangeName).Select(b => b!["routing_key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var eventType in topology.Subscriptions[subscriptionId].Bindings)
            {
                Assert.Contains(topology.RoutingKey(lane, eventType), routingKeys);
            }
            var dlq = await broker.QueueAsync(topology.DlqName(subscriptionId, lane));
            Assert.Equal("quorum", dlq.Type);
        }

        // Повторний declare — idempotent (readiness при кожному старті воркера).
        await using var channel = await connection.CreateChannelAsync();
        await topology.DeclareAsync(channel);
        Assert.Equal("4.3", broker.ServerVersion[..3]);
        Evidence.Record("G00_topology", new { window = "readiness/declare", server = broker.ServerVersion, image = RabbitMqFixture.Image, queues = expectedQueues, dlqs = expectedQueues, topology_version = topology.TopologyVersion, redeclare_idempotent = true });
    }

    [Fact]
    public async Task G01_FanOut_OnePublicationReachesEveryRequiredSubscriptionOnce()
    {
        var topology = broker.Topology;
        var store = FileStore.Temp("g01");
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var subscriptions = new[] { "normalizer", "message-analytics", "archive" }; // required для raw.stored
        await PurgeAsync(connection, subscriptions.Select(s => topology.QueueName(s, "live")).ToArray());
        var consumers = new List<InboxConsumer>();
        foreach (var subscription in subscriptions)
        {
            var consumer = new InboxConsumer(store, subscription);
            await consumer.StartAsync(connection, topology.QueueName(subscription, "live"));
            consumers.Add(consumer);
        }

        var eventId = publisher.Enqueue("live");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        await WaitUntilAsync(() => consumers.All(c => c.AckedCount == 1));

        foreach (var subscription in subscriptions)
        {
            Assert.Equal(1, store.Read(s => s.Effects[FileStore.InboxKey(subscription, eventId)]));
            Assert.Equal("completed", store.Read(s => s.Receipts[FileStore.InboxKey(subscription, eventId)]));
        }
        Assert.Equal(0, store.Read(s => s.DuplicatesSuppressed));
        Assert.NotNull(store.Read(s => s.Outbox[eventId].ConfirmedAt));
        // Підписка без binding на raw.stored (parser) нічого не отримала.
        Assert.Equal(0, (await broker.QueueAsync(topology.QueueName("parser", "live"))).Total);
        Evidence.Record("G01_fan_out", new { window = "gate: копія кожній required підписці", subscriptions, effects_per_subscription = 1, duplicates = store.Read(s => s.DuplicatesSuppressed), parser_live_total = 0 });
        foreach (var c in consumers)
        {
            await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task G02_CompetingConsumers_TwoReplicasShareJobsWithoutDuplicates()
    {
        const int n = 200;
        var topology = broker.Topology;
        var store = FileStore.Temp("g02");
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var queue = topology.QueueName("normalizer", "live");
        await PurgeAsync(connection, queue, topology.QueueName("archive", "live"), topology.QueueName("message-analytics", "live"));
        await using var replica1 = new InboxConsumer(store, "normalizer", "1");
        await using var replica2 = new InboxConsumer(store, "normalizer", "2");
        await replica1.StartAsync(connection, queue, prefetch: 5);
        await replica2.StartAsync(connection, queue, prefetch: 5);

        var ids = Enumerable.Range(0, n).Select(i => publisher.Enqueue("live", rawMessageId: 10_000 + i)).ToList();
        Assert.Equal(n, await publisher.RelayAsync(connection, Confirm));
        await WaitUntilAsync(() => replica1.AckedCount + replica2.AckedCount == n, TimeSpan.FromSeconds(60));

        Assert.True(replica1.AckedCount > 0 && replica2.AckedCount > 0, $"replicas: {replica1.AckedCount}/{replica2.AckedCount}");
        Assert.Equal(n, store.Read(s => s.Effects.Count));
        Assert.All(ids, id => Assert.Equal(1, store.Read(s => s.Effects[FileStore.InboxKey("normalizer", id)])));
        Assert.Equal(0, store.Read(s => s.DuplicatesSuppressed));
        var stats = await broker.WaitQueueAsync(queue, q => q.Total == 0);
        Assert.Equal(0, stats.Total);
        // Інші required підписки на raw.stored отримали свої копії (backlog без consumer — W10a).
        Assert.Equal(n, (await broker.WaitQueueAsync(topology.QueueName("archive", "live"), q => q.Ready >= n)).Ready);
        Evidence.Record("G02_competing_consumers", new { window = "gate: репліки ділять jobs", messages = n, replica1 = replica1.AckedCount, replica2 = replica2.AckedCount, effects = store.Read(s => s.Effects.Count), duplicates = store.Read(s => s.DuplicatesSuppressed), archive_backlog_without_consumer = n });
    }

    [Fact]
    public async Task G03_Lanes_HistoryPublicationDoesNotEnterLiveQueue()
    {
        var topology = broker.Topology;
        var store = FileStore.Temp("g03");
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var liveBefore = (await broker.QueueAsync(topology.QueueName("parser", "live"))).Total;
        var historyBefore = (await broker.QueueAsync(topology.QueueName("parser", "history"))).Total;

        publisher.Enqueue("history", eventType: "message.normalized");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));

        var history = await broker.WaitQueueAsync(topology.QueueName("parser", "history"), q => q.Total == historyBefore + 1);
        Assert.Equal(historyBefore + 1, history.Total);
        Assert.Equal(liveBefore, (await broker.QueueAsync(topology.QueueName("parser", "live"))).Total);
        // projection не має replay-черги (ADR-0002): track.changed у replay lane отримує лише message-analytics.replay.
        var analyticsReplayBefore = (await broker.QueueAsync(topology.QueueName("message-analytics", "replay"))).Total;
        publisher.Enqueue("replay", eventType: "track.changed");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        Assert.Equal(analyticsReplayBefore + 1, (await broker.WaitQueueAsync(topology.QueueName("message-analytics", "replay"), q => q.Total == analyticsReplayBefore + 1)).Total);
        Assert.DoesNotContain("replay", topology.Subscriptions["projection"].Lanes);
        Evidence.Record("G03_lanes", new { window = "lanes: history не потрапляє в live; projection без replay", parser_history_delta = 1, parser_live_delta = 0, analytics_replay_delta = 1 });
    }

    [Fact]
    public async Task G04_BusinessFailuresExhausted_QuarantineReceiptThenDlq()
    {
        var topology = broker.Topology;
        var store = FileStore.Temp("g04");
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var subscription = "finalizer";
        var queue = topology.QueueName(subscription, "live");
        var dlq = topology.DlqName(subscription, "live");
        await PurgeAsync(connection, queue, dlq);
        var maxAttempts = topology.QueuePolicies["required"].MaxDeliveryAttempts;
        await using var consumer = new InboxConsumer(store, subscription) { Behaviour = Behaviour.AlwaysFail, MaxAttempts = maxAttempts };
        await consumer.StartAsync(connection, queue, prefetch: 1);

        var eventId = publisher.Enqueue("live", eventType: "parse.completed");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        var key = FileStore.InboxKey(subscription, eventId);

        var dlqStats = await broker.WaitQueueAsync(dlq, q => q.Ready == 1, TimeSpan.FromSeconds(60));
        Assert.True(dlqStats.Ready == 1, $"dlq={dlqStats}, source={await broker.QueueAsync(queue)}, delivered={consumer.Delivered}");
        Assert.Equal(maxAttempts, consumer.Delivered);
        Assert.Equal(maxAttempts, store.Read(s => s.Attempts[key]));
        Assert.Equal("quarantined", store.Read(s => s.Receipts[key]));
        Assert.False(store.Read(s => s.Effects.ContainsKey(key)), "quarantined delivery must not count as a business effect");
        Assert.Equal(0, (await broker.WaitQueueAsync(queue, q => q.Total == 0)).Total);
        // Спостереження RabbitMQ 4.3: explicit nack(requeue=true) з consumer НЕ інкрементує x-delivery-count (лише x-acquired-count),
        // тому x-delivery-limit не є механізмом бізнес-retry; він — запобіжник crash-loop (G04b). Retries живуть у БД (ADR-0004 §6.3).
        Assert.Equal(0, consumer.MaxDeliveryCount);

        // Durable transfer (W6a-2): quarantine receipt уже committed; з DLQ повідомлення знімається окремим кроком, ідемпотентно.
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(dlq, autoAck: false);
        Assert.NotNull(delivery);
        Assert.Equal(eventId, delivery.BasicProperties.MessageId);
        store.Commit(s => s.Receipts[key] = "quarantined");
        await channel.BasicAckAsync(delivery.DeliveryTag, false);
        Assert.Equal(0, (await broker.WaitQueueAsync(dlq, q => q.Total == 0)).Total);
        Evidence.Record("G04_business_failures_quarantine", new { window = "W6a-2", max_attempts = maxAttempts, deliveries = consumer.Delivered, attempts_in_store = maxAttempts, receipt = "quarantined", effects = 0, dlq_after_transfer = 0, x_delivery_count_seen = consumer.MaxDeliveryCount, observation = "RabbitMQ 4.3: nack(requeue=true) з consumer не інкрементує x-delivery-count (лише x-acquired-count); business retries — у БД" });
    }

    [Fact]
    public async Task G04b_CrashLoop_DeliveryLimitDeadLettersAfterConfiguredRedeliveries()
    {
        var topology = broker.Topology;
        var store = FileStore.Temp("g04b");
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var subscription = "llm-worker";
        var queue = topology.QueueName(subscription, "live");
        var dlq = topology.DlqName(subscription, "live");
        await PurgeAsync(connection, queue, dlq);
        var deliveryLimit = (int)topology.QueuePolicies["required"].BrokerArguments["x-delivery-limit"];

        var eventId = publisher.Enqueue("live", eventType: "llm.requested");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));

        // Consumer «падає» до commit на кожній доставці (канал закривається без ACK) → broker рахує redeliveries.
        var deliveries = 0;
        long maxCount = 0;
        for (var i = 0; i <= deliveryLimit + 1; i++)
        {
            await using var crashing = new InboxConsumer(store, subscription, $"crash-{i}") { Behaviour = Behaviour.CrashBeforeCommit };
            await crashing.StartAsync(connection, queue, prefetch: 1);
            var crashed = await Task.WhenAny(crashing.Crashed, Task.Delay(TimeSpan.FromSeconds(5)));
            if (crashed != crashing.Crashed)
            {
                break; // нічого не доставлено: повідомлення вже dead-lettered
            }
            deliveries += crashing.Delivered;
            maxCount = Math.Max(maxCount, crashing.MaxDeliveryCount);
        }

        var dlqStats = await broker.WaitQueueAsync(dlq, q => q.Ready == 1, TimeSpan.FromSeconds(30));
        Assert.True(dlqStats.Ready == 1, $"dlq={dlqStats}, source={await broker.QueueAsync(queue)}, deliveries={deliveries}, maxCount={maxCount}");
        Assert.Equal(deliveryLimit + 1, deliveries);      // 1 initial + x-delivery-limit redeliveries
        Assert.Equal(deliveryLimit, maxCount);            // x-delivery-count header дійшов до ліміту
        Assert.Equal(0, (await broker.WaitQueueAsync(queue, q => q.Total == 0)).Total);
        Assert.False(store.Read(s => s.Effects.ContainsKey(FileStore.InboxKey(subscription, eventId))));
        // Оператор бачить DLQ; receipt quarantined тут ставить reconciliation/DLQ consumer (P03), не сам crash-loop.
        Evidence.Record("G04b_crash_loop_delivery_limit", new { window = "W4 loop → x-delivery-limit", x_delivery_limit = deliveryLimit, deliveries, max_x_delivery_count = maxCount, dlq_ready = 1, effects = 0 });
    }

    [Fact(Skip = "Loss of quorum потребує кластера з 3 вузлів; spike працює на одному Testcontainers-вузлі (single-member quorum). HA — P16/окремий harness.")]
    public void G05_LossOfQuorum_ThreeNodeCluster()
    {
    }
}

[CollectionDefinition(Name)]
public sealed class GateCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "gate-broker";
}
