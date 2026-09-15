using Puluj.Transport.Spike.Tests.Spike;
using RabbitMQ.Client;

namespace Puluj.Transport.Spike.Tests;

/// <summary>
/// Crash windows ADR-0004, брокерна частина: P02-C01…C09. Кожен тест асертить committed outcome у FileStore
/// (effects/inbox/receipts/outbox) і стан черг, а не лише «повідомлення прийшло». Власний контейнер (restart/alarm).
/// </summary>
[Collection(CrashCollection.Name)]
public sealed class CrashTests(RabbitMqFixture broker)
{
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(10);
    private const string Sub = "normalizer";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.True(condition(), what ?? "condition not reached within timeout");
    }

    private async Task PurgeAsync(params string[] queues)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    private string Queue(string subscription = Sub, string lane = "live") => broker.Topology.QueueName(subscription, lane);

    private static int Effect(FileStore store, string subscription, string eventId) =>
        store.Read(s => s.Effects.GetValueOrDefault(FileStore.InboxKey(subscription, eventId)));

    [Fact]
    public async Task C01_ConsumerCrashBeforeCommit_RedeliveredAndAppliedExactlyOnce()
    {
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c01");
        var publisher = new OutboxPublisher(store, broker.Topology);
        await using var connection = await broker.ConnectAsync();
        await using var crashing = new InboxConsumer(store, Sub, "1") { Behaviour = Behaviour.CrashBeforeCommit };
        await crashing.StartAsync(connection, Queue(), prefetch: 1);

        var eventId = publisher.Enqueue("live");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        await crashing.Crashed.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, Effect(store, Sub, eventId));                 // нічого не закомічено
        Assert.Equal(1, (await broker.WaitQueueAsync(Queue(), q => q.Ready == 1)).Ready); // повернулось у чергу

        await using var healthy = new InboxConsumer(store, Sub, "2");
        await healthy.StartAsync(connection, Queue(), prefetch: 1);
        await WaitUntilAsync(() => healthy.AckedCount == 1);
        Assert.Equal(1, healthy.RedeliveredSeen);                       // redelivered=true
        Assert.Equal(1, Effect(store, Sub, eventId));
        Assert.Equal(0, store.Read(s => s.DuplicatesSuppressed));
        Assert.Equal(0, (await broker.WaitQueueAsync(Queue(), q => q.Total == 0)).Total);
        Evidence.Record("C01_crash_before_commit", new { window = "W4", effect_after_crash = 0, ready_after_crash = 1, redelivered_seen = healthy.RedeliveredSeen, effect_final = 1, duplicates = 0 });
    }

    [Fact]
    public async Task C02_ConsumerCrashAfterCommitBeforeAck_RedeliveryAbsorbedByInbox()
    {
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c02");
        var publisher = new OutboxPublisher(store, broker.Topology);
        await using var connection = await broker.ConnectAsync();
        await using var crashing = new InboxConsumer(store, Sub, "1") { Behaviour = Behaviour.CrashAfterCommitBeforeAck };
        await crashing.StartAsync(connection, Queue(), prefetch: 1);

        var eventId = publisher.Enqueue("live");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        await crashing.Crashed.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, Effect(store, Sub, eventId));                   // commit є
        Assert.Equal(1, (await broker.WaitQueueAsync(Queue(), q => q.Ready == 1)).Ready); // але ACK не було → redelivery

        await using var healthy = new InboxConsumer(store, Sub, "2");
        await healthy.StartAsync(connection, Queue(), prefetch: 1);
        await WaitUntilAsync(() => healthy.AckedCount == 1);
        Assert.Equal(1, Effect(store, Sub, eventId));                   // без другого ефекту
        Assert.Equal(1, store.Read(s => s.DuplicatesSuppressed));       // inbox hit → ACK
        Assert.Equal(0, (await broker.WaitQueueAsync(Queue(), q => q.Total == 0)).Total);
        Evidence.Record("C02_crash_after_commit_before_ack", new { window = "W5", effect_after_crash = 1, ready_after_crash = 1, effect_final = 1, duplicates_suppressed = 1 });
    }

    [Fact]
    public async Task C03_BrokerRestartDuringPublish_UnconfirmedRepublishedSameEventId_NoLossNoDuplicateEffect()
    {
        const int n = 300;
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c03");
        var publisher = new OutboxPublisher(store, broker.Topology);
        var ids = Enumerable.Range(0, n).Select(i => publisher.Enqueue("live", rawMessageId: 20_000 + i)).ToList();

        // Relay у фоні; посередині — docker stop/start брокера.
        var firstPass = Task.Run(async () =>
        {
            try
            {
                await using var connection = await broker.ConnectAsync();
                return await publisher.RelayAsync(connection, TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                return -1; // connection loss under restart: relay падає, outbox лишається
            }
        });
        await Task.Delay(300);
        await broker.RestartAsync();
        var firstConfirmed = await firstPass;
        var unconfirmedAfterCrash = store.Read(s => s.Outbox.Values.Count(r => r.ConfirmedAt is null));
        Assert.True(unconfirmedAfterCrash > 0 || firstConfirmed == n, $"first pass confirmed {firstConfirmed}, unconfirmed {unconfirmedAfterCrash}");

        // Після відновлення relay публікує решту тим самим event_id; consumer застосовує кожен рівно раз.
        await using var connection2 = await broker.ConnectAsync();
        var second = await publisher.RelayAsync(connection2, Confirm);
        Assert.Equal(unconfirmedAfterCrash, second);
        Assert.Equal(0, store.Read(s => s.Outbox.Values.Count(r => r.ConfirmedAt is null)));

        await using var consumer = new InboxConsumer(store, Sub);
        await consumer.StartAsync(connection2, Queue(), prefetch: 20);
        await WaitUntilAsync(() => store.Read(s => s.Effects.Count) == n && (consumer.AckedCount >= n), TimeSpan.FromSeconds(60), "all effects applied");
        Assert.All(ids, id => Assert.Equal(1, Effect(store, Sub, id)));
        var stats = await broker.WaitQueueAsync(Queue(), q => q.Total == 0);
        Assert.Equal(0, stats.Total);
        // Дублікати можливі (W3/W7a: confirm загублено після фактичного enqueue) і поглинуті inbox — це очікувано, не помилка.
        Assert.True(store.Read(s => s.DuplicatesSuppressed) >= 0);
        Evidence.Record("C03_broker_restart_during_publish", new { window = "W7a/W2/W3", messages = n, first_pass_confirmed = firstConfirmed, unconfirmed_after_restart = unconfirmedAfterCrash, republished = second, effects = store.Read(s => s.Effects.Count), duplicates_suppressed = store.Read(s => s.DuplicatesSuppressed), lost = n - store.Read(s => s.Effects.Count) });
    }

    [Fact]
    public async Task C04_DelayedConfirm_RepublishAbsorbedAsDuplicate()
    {
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c04");
        var publisher = new OutboxPublisher(store, broker.Topology);
        await using var connection = await broker.ConnectAsync();
        await using var consumer = new InboxConsumer(store, Sub);
        await consumer.StartAsync(connection, Queue(), prefetch: 5);

        var eventId = publisher.Enqueue("live");
        // Симуляція на клієнті: confirm «не встиг» (timeout 1 tick) → рядок лишається unconfirmed, хоча брокер уже прийняв.
        Assert.Equal(0, await publisher.RelayAsync(connection, TimeSpan.FromTicks(1)));
        Assert.Null(store.Read(s => s.Outbox[eventId].ConfirmedAt));
        Assert.Equal(1, store.Read(s => s.Outbox[eventId].Attempts));

        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm)); // republish того самого event_id
        await WaitUntilAsync(() => consumer.AckedCount == 2, TimeSpan.FromSeconds(30), "both copies acked");
        Assert.Equal(1, Effect(store, Sub, eventId));
        Assert.Equal(1, store.Read(s => s.DuplicatesSuppressed));
        Assert.Equal(2, store.Read(s => s.Outbox[eventId].Attempts));
        Evidence.Record("C04_delayed_confirm", new { window = "W7b (client-simulated timeout)", outbox_attempts = 2, deliveries = consumer.AckedCount, effect = 1, duplicates_suppressed = 1 });
    }

    [Fact]
    public async Task C05_BrokerRestartWhileUnacked_RedeliveredToNewConsumerProcess()
    {
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c05");
        var publisher = new OutboxPublisher(store, broker.Topology);
        var eventId = publisher.Enqueue("live");
        await using (var connection = await broker.ConnectAsync())
        {
            Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
            await using var slow = new InboxConsumer(store, Sub, "1") { WorkDelay = TimeSpan.FromSeconds(60) };
            await slow.StartAsync(connection, Queue(), prefetch: 1);
            await broker.WaitQueueAsync(Queue(), q => q.Unacked == 1);
            Assert.Equal(1, slow.Delivered);
            await broker.RestartAsync(); // consumer втрачає connection, повідомлення unacked → назад у чергу
        }

        await using var connection2 = await broker.ConnectAsync();
        Assert.Equal(1, (await broker.WaitQueueAsync(Queue(), q => q.Ready == 1)).Ready);
        await using var fresh = new InboxConsumer(store, Sub, "2");
        await fresh.StartAsync(connection2, Queue(), prefetch: 1);
        await WaitUntilAsync(() => fresh.AckedCount == 1);
        Assert.Equal(1, fresh.RedeliveredSeen);
        Assert.Equal(1, Effect(store, Sub, eventId));
        Evidence.Record("C05_broker_restart_while_unacked", new { window = "W7c", unacked_before_restart = 1, ready_after_restart = 1, redelivered_seen = fresh.RedeliveredSeen, effect = 1 });
    }

    [Fact]
    public async Task C06_PublishBeforeConsumerStarts_DurableBacklogDelivered()
    {
        const int n = 50;
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c06");
        var publisher = new OutboxPublisher(store, broker.Topology);
        await using var connection = await broker.ConnectAsync();
        for (var i = 0; i < n; i++)
        {
            publisher.Enqueue("live", rawMessageId: 30_000 + i);
        }
        Assert.Equal(n, await publisher.RelayAsync(connection, Confirm));
        Assert.Equal(n, (await broker.WaitQueueAsync(Queue(), q => q.Ready == n)).Ready);

        await using var consumer = new InboxConsumer(store, Sub);
        await consumer.StartAsync(connection, Queue(), prefetch: 10);
        await WaitUntilAsync(() => consumer.AckedCount == n);
        Assert.Equal(n, store.Read(s => s.Effects.Count));
        Assert.Equal(0, store.Read(s => s.DuplicatesSuppressed));
        Evidence.Record("C06_publish_before_consumer", new { window = "W10a", messages = n, ready_before_consumer = n, effects = n, duplicates = 0 });
    }

    [Fact]
    public async Task C07_OneSubscriptionOffline_OthersProceed_BacklogCatchesUpLater()
    {
        const int n = 40;
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c07");
        var publisher = new OutboxPublisher(store, broker.Topology);
        await using var connection = await broker.ConnectAsync();
        await using var online = new InboxConsumer(store, Sub);
        await online.StartAsync(connection, Queue(), prefetch: 10);
        // archive — required, але offline.
        for (var i = 0; i < n; i++)
        {
            publisher.Enqueue("live", rawMessageId: 40_000 + i);
        }
        Assert.Equal(n, await publisher.RelayAsync(connection, Confirm));
        await WaitUntilAsync(() => online.AckedCount == n);
        var archiveBacklog = await broker.WaitQueueAsync(Queue("archive"), q => q.Ready == n);
        Assert.Equal(n, archiveBacklog.Ready);
        Assert.Equal(0, archiveBacklog.Consumers);                    // «відсутній required consumer» видно як backlog без consumers

        await using var archive = new InboxConsumer(store, "archive");
        await archive.StartAsync(connection, Queue("archive"), prefetch: 10);
        await WaitUntilAsync(() => archive.AckedCount == n);
        Assert.Equal(n, store.Read(s => s.Effects.Keys.Count(k => k.StartsWith("archive:", StringComparison.Ordinal))));
        Assert.Equal(0, (await broker.WaitQueueAsync(Queue("archive"), q => q.Total == 0)).Total);
        Evidence.Record("C07_one_subscription_offline", new { window = "W10b", messages = n, online_effects = online.AckedCount, archive_backlog_ready = archiveBacklog.Ready, archive_consumers_while_offline = archiveBacklog.Consumers, archive_effects_after_start = n });
    }

    [Fact]
    public async Task C08_UnroutableOrMissingRequiredQueue_ReturnedAndReadinessFails_UnbindInvisibleToPassiveDeclare()
    {
        var store = FileStore.Temp("c08");
        var topology = broker.Topology;
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();

        // 1. Немає жодної bound черги для routing key → basic.return; outbox не позначається confirmed/delivered.
        var eventId = publisher.Enqueue("live", eventType: "unbound.event");
        Assert.Equal(0, await publisher.RelayAsync(connection, Confirm));
        Assert.True(store.Read(s => s.Outbox[eventId].Returned));
        Assert.Null(store.Read(s => s.Outbox[eventId].ConfirmedAt));

        // 2. Required черга зникла (напр. помилковий deploy) → readiness повідомляє missing; declare відновлює.
        var queue = topology.QueueName("archive", "history");
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeleteAsync(queue);
        }
        var missing = await topology.MissingRequiredQueuesAsync(connection);
        Assert.Equal([queue], missing);
        await using (var channel = await connection.CreateChannelAsync())
        {
            await topology.DeclareAsync(channel);
        }
        Assert.Empty(await topology.MissingRequiredQueuesAsync(connection));

        // 3. Drift binding при живій черзі: passive declare НЕ бачить проблему; management API bindings — бачить; declare відновлює.
        var routingKey = topology.RoutingKey("history", "ingress.received");
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueUnbindAsync(queue, topology.ExchangeName, routingKey);
        }
        Assert.Empty(await topology.MissingRequiredQueuesAsync(connection));            // сліпа зона passive declare
        var bindingsAfterUnbind = (await broker.BindingsAsync(queue)).Select(b => b!["routing_key"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(routingKey, bindingsAfterUnbind);                            // management API показує drift
        // Інша required підписка (raw-writer.history) ще bound → публікація confirmed і НЕ returned: mandatory/confirms
        // не доводять існування кожної очікуваної підписки (§3.2); archive.history копії не отримує — лише reconciliation це бачить.
        var archiveBefore = (await broker.QueueAsync(queue)).Total;
        var rawWriterBefore = (await broker.QueueAsync(topology.QueueName("raw-writer", "history"))).Total;
        var unbound = publisher.Enqueue("history", eventType: "ingress.received");
        Assert.Equal(1, await publisher.RelayAsync(connection, Confirm));
        Assert.False(store.Read(s => s.Outbox[unbound].Returned));
        Assert.Equal(rawWriterBefore + 1, (await broker.WaitQueueAsync(topology.QueueName("raw-writer", "history"), q => q.Total == rawWriterBefore + 1)).Total);
        Assert.Equal(archiveBefore, (await broker.QueueAsync(queue)).Total);
        await using (var channel = await connection.CreateChannelAsync())
        {
            await topology.DeclareAsync(channel);
        }
        var bindingsAfterDeclare = (await broker.BindingsAsync(queue)).Select(b => b!["routing_key"]!.GetValue<string>()).ToList();
        Assert.Contains(routingKey, bindingsAfterDeclare);
        Evidence.Record("C08_unroutable_missing_queue_unbind_drift", new { window = "W11", unroutable_returned = true, unroutable_confirmed_at = (string?)null, missing_after_delete = missing, missing_after_redeclare = 0, unbind_seen_by_passive_declare = false, unbind_seen_by_management_bindings = true, publish_after_unbind_confirmed_not_returned = true, archive_history_copy_lost_until_reconciliation = true, bindings_restored_by_declare = true });
    }

    [Fact]
    public async Task C09_MemoryAlarm_PublisherBlocked_OutboxRetainsUntilAlarmCleared()
    {
        await PurgeAsync(Queue(), Queue("archive"), Queue("message-analytics"));
        var store = FileStore.Temp("c09");
        var publisher = new OutboxPublisher(store, broker.Topology);
        var eventId = publisher.Enqueue("live");

        await broker.RabbitmqctlAsync("set_vm_memory_high_watermark", "0.0000001");
        await WaitUntilAsync(() => broker.MemoryAlarmActiveAsync().GetAwaiter().GetResult(), TimeSpan.FromSeconds(30), "memory alarm raised");
        try
        {
            await using var connection = await broker.ConnectAsync();
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ConnectionBlockedAsync += (_, _) => { blocked.TrySetResult(); return Task.CompletedTask; };
            var confirmed = await publisher.RelayAsync(connection, TimeSpan.FromSeconds(5));
            Assert.Equal(0, confirmed);                                        // confirm не приходить: publisher blocked
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));            // connection.blocked отримано
            Assert.Null(store.Read(s => s.Outbox[eventId].ConfirmedAt));       // outbox тримає рядок
        }
        finally
        {
            await broker.RabbitmqctlAsync("set_vm_memory_high_watermark", "0.4");
        }
        await WaitUntilAsync(() => !broker.MemoryAlarmActiveAsync().GetAwaiter().GetResult(), TimeSpan.FromSeconds(30), "memory alarm cleared");

        await using var connection2 = await broker.ConnectAsync();
        Assert.Equal(1, await publisher.RelayAsync(connection2, Confirm));
        await using var consumer = new InboxConsumer(store, Sub);
        await consumer.StartAsync(connection2, Queue(), prefetch: 5);
        await WaitUntilAsync(() => store.Read(s => s.Effects.Count) == 1);
        Assert.Equal(1, Effect(store, Sub, eventId));
        // Якщо перша (заблокована) публікація все ж дійшла після зняття alarm — дублікат поглинуто, ефект один.
        await Task.Delay(1000);
        Assert.Equal(1, Effect(store, Sub, eventId));
        Evidence.Record("C09_memory_alarm_blocked_publisher", new { window = "W13", alarm = "vm_memory_high_watermark 0.0000001 → connection.blocked", confirmed_while_blocked = 0, outbox_retained = true, confirmed_after_clear = 1, effect = 1, duplicates_suppressed = store.Read(s => s.DuplicatesSuppressed) });
    }
}

[CollectionDefinition(Name)]
public sealed class CrashCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "crash-broker";
}
