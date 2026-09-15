using System.Diagnostics;
using System.Text.Json;
using Puluj.Transport.Spike.Tests.Spike;

namespace Puluj.Transport.Spike.Tests;

/// <summary>
/// Smoke load (plan P02 крок 7): не SLO і не baseline §13 — лише evidence, що fan-out + competing consumers + confirms
/// працюють на тисячах повідомлень без втрат/дублікатів, з порядковими числами throughput/latency для P16.
/// </summary>
[Collection(LoadCollection.Name)]
public sealed class LoadSmokeTests(RabbitMqFixture broker)
{
    [Fact]
    public async Task L01_TwoSubscriptionsTwoReplicas_5000Messages_NoLossNoDuplicates_MetricsRecorded()
    {
        const int n = 5000;
        const int padding = 2048;
        var topology = broker.Topology;
        var store = FileStore.Temp("load", persist: false);
        var publisher = new OutboxPublisher(store, topology);
        await using var connection = await broker.ConnectAsync();
        var queues = new[] { topology.QueueName("normalizer", "live"), topology.QueueName("archive", "live") };
        await using (var channel = await connection.CreateChannelAsync())
        {
            foreach (var q in queues.Append(topology.QueueName("message-analytics", "live")))
            {
                await channel.QueuePurgeAsync(q);
            }
        }

        var consumers = new List<InboxConsumer>();
        foreach (var subscription in new[] { "normalizer", "archive" })
        {
            foreach (var replica in new[] { "1", "2" })
            {
                var consumer = new InboxConsumer(store, subscription, replica);
                await consumer.StartAsync(connection, topology.QueueName(subscription, "live"), prefetch: 50);
                consumers.Add(consumer);
            }
        }

        var enqueueWatch = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            publisher.Enqueue("live", rawMessageId: 100_000 + i, paddingBytes: padding);
        }
        enqueueWatch.Stop();

        var publishWatch = Stopwatch.StartNew();
        var confirmed = await publisher.RelayAsync(connection, TimeSpan.FromSeconds(30));
        publishWatch.Stop();
        Assert.Equal(n, confirmed);

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (consumers.Sum(c => c.AckedCount) < 2 * n && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.Equal(2 * n, consumers.Sum(c => c.AckedCount));
        Assert.Equal(2 * n, store.Read(s => s.Effects.Count));
        Assert.Equal(0, store.Read(s => s.DuplicatesSuppressed));
        Assert.Equal(0, store.Read(s => s.Redelivered));
        foreach (var q in queues)
        {
            Assert.Equal(0, (await broker.WaitQueueAsync(q, s => s.Total == 0)).Total);
        }
        Assert.All(consumers, c => Assert.True(c.AckedCount > 0));

        // Latency: outbox enqueue → inbox completion (у межах одного процесу, той самий годинник).
        var completions = store.Read(s => s.Inbox.Values.Select(v => v.UtcDateTime).OrderBy(t => t).ToList());
        var firstCompletion = completions.First();
        var lastCompletion = completions.Last();
        var drainSeconds = (lastCompletion - firstCompletion).TotalSeconds;

        var metrics = new
        {
            task = "P02",
            recorded_at = DateTime.UtcNow,
            environment = new
            {
                os = Environment.OSVersion.ToString(),
                cpu_count = Environment.ProcessorCount,
                rabbitmq_server = broker.ServerVersion,
                rabbitmq_image = RabbitMqFixture.Image,
                client = "RabbitMQ.Client 7.2.2",
                testcontainers = "Testcontainers.RabbitMq 4.15.0",
                note = "single-node quorum queues, Testcontainers on the dev host; in-memory FileStore (persist=false); NOT an SLO measurement (plan §13 baseline method is P16)",
            },
            workload = new { messages = n, payload_bytes_padding = padding, subscriptions = 2, replicas_per_subscription = 2, prefetch = 50, publisher_confirms = "per message, sequential" },
            results = new
            {
                enqueue_ms = enqueueWatch.ElapsedMilliseconds,
                publish_confirm_ms = publishWatch.ElapsedMilliseconds,
                publish_confirm_rate_per_s = Math.Round(n / publishWatch.Elapsed.TotalSeconds, 1),
                deliveries_acked = consumers.Sum(c => c.AckedCount),
                effects = store.Read(s => s.Effects.Count),
                duplicates_suppressed = store.Read(s => s.DuplicatesSuppressed),
                redelivered = store.Read(s => s.Redelivered),
                consumer_drain_seconds_first_to_last_completion = Math.Round(drainSeconds, 3),
                consumer_rate_per_s = Math.Round(2 * n / Math.Max(drainSeconds, 0.001), 1),
                per_consumer_acked = consumers.Select(c => c.AckedCount).ToArray(),
            },
        };
        var evidence = Evidence.Path_("P02-spike-metrics.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        foreach (var c in consumers)
        {
            await c.DisposeAsync();
        }
    }

}

[CollectionDefinition(Name)]
public sealed class LoadCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "load-broker";
}
