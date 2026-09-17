using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Messaging;
using Puluj.Processing.Stages;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P16 release gate.  These are deliberately integration tests: every completion is a committed PostGIS receipt
/// after an AMQP delivery from a real RabbitMQ Testcontainer.  The load profile is an archive-only, independent
/// branch benchmark; it reports a scaling curve, rather than pretending that a developer laptop establishes a
/// production SLO.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class P16ReleaseGateTests(MessagingFixture f)
{
    private const int LoadMessages = 96;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Mutex EvidenceMutex = new(false, @"Global\PulujG.P16Evidence");

    private static void RecordEvidence(string scenario, object values)
    {
        var path = MessagingFixture.EvidencePath("P16-release-evidence.json");
        EvidenceMutex.WaitOne();
        try
        {
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            root["$comment"] = "P16 release-gate facts from real Testcontainers PostGIS/RabbitMQ runs; P03–P15 evidence is stored separately.";
            var entry = JsonSerializer.SerializeToNode(values)!.AsObject();
            entry["recorded_at"] = DateTimeOffset.UtcNow;
            root[scenario] = entry;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        finally
        {
            EvidenceMutex.ReleaseMutex();
        }
    }

    private async Task StartPlatformAsync()
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Parser.StartAsync(None);
        await f.Finalizer.StartAsync(None);
        await f.TrackWorker.StartAsync(None);
        await f.AlertWorker.StartAsync(None);
        await f.IncidentWorker.StartAsync(None);
        await f.Projection.StartAsync(None);
    }

    private async Task StopPlatformAsync()
    {
        await f.Projection.StopAsync(None);
        await f.IncidentWorker.StopAsync(None);
        await f.AlertWorker.StopAsync(None);
        await f.TrackWorker.StopAsync(None);
        await f.Finalizer.StopAsync(None);
        await f.Parser.StopAsync(None);
        await f.Normalizer.StopAsync(None);
        await f.Archive.StopAsync(None);
        await f.RawWriter.StopAsync(None);
        await f.Relay.StopAsync(None);
    }

    /// <summary>
    /// The canary path uses the new ingress and all platform writers.  Calling the legacy processor afterwards is
    /// intentional: its ownership guard must see observation-backed rows and create no second target/track/alert.
    /// </summary>
    [Fact]
    public async Task G01_Canary_ingress_to_map_has_committed_results_and_legacy_cannot_be_a_second_writer()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var messages = new[]
        {
            ("p16-canary-1", "Шахеди на Сумщині курсом на Полтавщину."),
            ("p16-canary-2", "Вибухи у Харкові."),
            ("p16-canary-3", "Повітряна тривога в Київській області."),
            ("p16-canary-4", "Відбій повітряної тривоги в Київській області."),
        };

        foreach (var (id, text) in messages)
        {
            await f.Ingress.PublishAsync(f.Message(id, text), source, "p16-canary", null, live: true, None);
        }

        await StartPlatformAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(
                async () => await f.CountAsync("processing.extractions") == messages.Length,
                TimeSpan.FromSeconds(60)), "all canary roots were finalized");
            Assert.True(await MessagingFixture.WaitUntilAsync(
                async () => await f.CountAsync("processing.deliveries", "outcome IS NULL AND subscription_id <> 'message-analytics'") == 0,
                TimeSpan.FromSeconds(60)), "all non-analytics deliveries are terminal");
            Assert.True(await MessagingFixture.WaitUntilAsync(
                async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0,
                TimeSpan.FromSeconds(40)), "outbox confirmed");
        }
        finally
        {
            await StopPlatformAsync();
        }

        var rawIds = await f.ScalarAsync<string>("SELECT string_agg(raw_message_id::text, ',' ORDER BY raw_message_id) FROM raw_messages");
        foreach (var rawId in rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse))
        {
            Assert.Equal(0, await f.LegacyProcessor.ProcessAsync(rawId, None));
        }

        Assert.Equal(messages.Length, await f.CountAsync("raw_messages"));
        Assert.Equal(0, await f.CountAsync("targets", "observation_id IS NULL"));
        Assert.True(await f.CountAsync("targets", "observation_id IS NOT NULL") >= 2, "target facts are written by the canary owner");
        Assert.Equal(0, await f.CountAsync("processing.deliveries", "outcome IS NULL AND subscription_id <> 'message-analytics'"));
        Assert.Equal(0, await f.CountAsync("messaging.outbox", "confirmed_at IS NULL"));
        RecordEvidence("P16-G01", new
        {
            roots = messages.Length,
            target_writer = "platform observation-backed targets",
            legacy_processor_after_canary = "0 effects for every root",
            pending_non_analytics_deliveries = 0,
            unconfirmed_outbox = 0,
        });
    }

    /// <summary>
    /// A small, repeatable scaling matrix.  Archive is selected because it is an independent durable branch with
    /// a real DB effect; normalizer/parser are intentionally left offline so their backlog cannot masquerade as a
    /// completed outcome.  The generated evidence contains p50/p95/p99 commit latency and drain rate for 1/2/4/8
    /// competing consumers.  It is a release input, not a fabricated capacity claim.
    /// </summary>
    [Fact]
    public async Task L01_Archive_branch_records_committed_scaling_matrix_for_1_2_4_8_replicas()
    {
        var matrix = new List<object>();
        foreach (var replicas in new[] { 1, 2, 4, 8 })
        {
            await f.ResetAsync();
            var consumers = new List<SubscriptionConsumer> { f.Archive };
            var handler = f.Services.GetRequiredService<ArchiveHandler>();
            for (var replica = 2; replica <= replicas; replica++)
            {
                consumers.Add(f.NewConsumer(handler, $"archive@p16-{replicas}-{replica}"));
            }

            var stopwatch = Stopwatch.StartNew();
            await f.Relay.StartAsync(None);
            foreach (var consumer in consumers)
            {
                await consumer.StartAsync(None);
            }
            try
            {
                for (var i = 0; i < LoadMessages; i++)
                {
                    await f.IngestAsync($"p16-load-{replicas}-{i}", $"P16 load sample {i}: інформаційне повідомлення без фактів.");
                }

                Assert.True(await MessagingFixture.WaitUntilAsync(
                    async () => await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome = 'completed'") == LoadMessages,
                    TimeSpan.FromSeconds(90)), $"archive completions for {replicas} replicas");
                Assert.Equal(LoadMessages, await f.CountAsync("messaging.events", "event_type = 'raw.stored'"));
                Assert.Equal(0, await f.CountAsync("messaging.inbox", "subscription_id = 'archive' AND outcome <> 'completed'"));

                var p50 = await f.ScalarAsync<double>("""
                    SELECT percentile_cont(0.50) WITHIN GROUP (ORDER BY extract(epoch FROM (completed_at - expected_at)) * 1000)
                    FROM processing.deliveries WHERE subscription_id = 'archive' AND outcome = 'completed'
                    """);
                var p95 = await f.ScalarAsync<double>("""
                    SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM (completed_at - expected_at)) * 1000)
                    FROM processing.deliveries WHERE subscription_id = 'archive' AND outcome = 'completed'
                    """);
                var p99 = await f.ScalarAsync<double>("""
                    SELECT percentile_cont(0.99) WITHIN GROUP (ORDER BY extract(epoch FROM (completed_at - expected_at)) * 1000)
                    FROM processing.deliveries WHERE subscription_id = 'archive' AND outcome = 'completed'
                    """);
                stopwatch.Stop();
                matrix.Add(new
                {
                    replicas,
                    roots = LoadMessages,
                    committed_outcomes = LoadMessages,
                    drain_ms = stopwatch.ElapsedMilliseconds,
                    committed_per_second = Math.Round(LoadMessages / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), 2),
                    commit_latency_ms = new { p50 = Math.Round(p50, 3), p95 = Math.Round(p95, 3), p99 = Math.Round(p99, 3) },
                    per_consumer_deliveries = consumers.Select(c => c.Delivered).ToArray(),
                });
            }
            finally
            {
                foreach (var consumer in consumers.AsEnumerable().Reverse())
                {
                    await consumer.StopAsync(None);
                }
                await f.Relay.StopAsync(None);
            }
        }

        RecordEvidence("P16-L01", new
        {
            workload = "archive-only independent durable branch; real RabbitMQ + PostGIS; no LLM provider",
            matrix,
            interpretation = "compare this curve with the release environment baseline; it is not an absolute SLO",
        });
    }
}
