using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Infrastructure.Messaging;
using Puluj.Processing.Projection;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P11 (ADR-0011): the projection consumer turns `incident.changed` into one NOTIFY per delivery, and every API replica
/// (one LISTEN connection each) receives it — Postgres NOTIFY is the backplane. Redelivery is an inbox duplicate with no
/// second NOTIFY (at-most-once: the clients' recovery is a reload, never a replayed packet); a replay lane or an inactive
/// generation never reaches the live map; a LISTEN reconnect yields the resync marker; a burst of 1 000 events reaches
/// both replicas.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class ProjectionTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>One "API replica": a PgNotifyListener over the fixture's database collecting IncidentChanged and reconnect markers.</summary>
    private sealed class Replica : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        public readonly ConcurrentBag<PulujEvent> Incidents = [];
        public readonly ConcurrentBag<PulujEvent> Reconnects = [];
        public readonly TaskCompletionSource Listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Replica(MessagingFixture f)
        {
            var listener = new PgNotifyListener(f.Services.GetRequiredService<IConfiguration>(), NullLogger<PgNotifyListener>.Instance);
            _pump = Task.Run(async () =>
            {
                await foreach (var evt in listener.ListenAsync(_cts.Token))
                {
                    if (evt.Type == PulujEventType.IncidentChanged)
                    {
                        Incidents.Add(evt);
                    }
                    else if (evt.Type == PulujEventType.ListenerReconnected)
                    {
                        Reconnects.Add(evt);
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // the pump is cancelled; a late failure is not the test's concern
            }
        }
    }

    private static async Task<bool> WaitListeningAsync(MessagingFixture f, int listeners) =>
        await MessagingFixture.WaitUntilAsync(async () => await f.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE query ILIKE 'LISTEN %'") >= listeners, TimeSpan.FromSeconds(20));

    private static byte[] IncidentChanged(Guid eventId, long incidentId, int revision, string change, string lane, Guid? generation) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["event_id"] = eventId.ToString(),
            ["event_type"] = "incident.changed",
            ["schema_version"] = "1.0",
            ["producer"] = "incident-worker@test",
            ["occurred_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["published_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["correlation_id"] = Guid.CreateVersion7().ToString(),
            ["causation_id"] = Guid.CreateVersion7().ToString(),
            ["traceparent"] = RawStoredEnvelope.CurrentTraceparent(),
            ["processing_run_id"] = Guid.CreateVersion7().ToString(),
            ["pipeline_version"] = "test",
            ["topology_version"] = 10,
            ["lane"] = lane,
            ["aggregate_id"] = $"incident:{incidentId}",
            ["aggregate_revision"] = revision,
            ["partition_key"] = "incident:kind:impact.explosion.reported",
            ["payload"] = new JsonObject
            {
                ["incident_id"] = incidentId,
                ["change"] = change,
                ["revision"] = revision,
                ["effective_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["observation_ids"] = new JsonArray(),
                ["event_kind_code"] = "impact.explosion.reported",
                ["state"] = "reported",
                ["generation_id"] = (generation ?? Puluj.Processing.Incidents.IncidentStateWriter.LiveGeneration).ToString(),
                ["policy_version"] = "incident-1/p2",
            },
        }.ToJsonString());

    [Fact]
    public async Task R05_Projection_notifies_once_per_delivery_and_every_replica_hears_it()
    {
        await f.ResetAsync();
        await f.ExecAsync("INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES (@live, true, now()), (@shadow, false, now())",
            ("live", Puluj.Processing.Incidents.IncidentStateWriter.LiveGeneration), ("shadow", Guid.Parse("00000000-0000-0000-0000-00000000dead")));
        await using var replica1 = new Replica(f);
        await using var replica2 = new Replica(f);
        Assert.True(await WaitListeningAsync(f, 2), "two LISTEN connections");
        await f.Projection.StartAsync(None);
        try
        {
            // A real incident.changed from the incident-worker path (P10 I01) would do the same; here the envelope is published straight to the exchange.
            var created = Guid.CreateVersion7();
            await f.PublishRawAsync("puluj.live.incident.changed", IncidentChanged(created, 1, 1, "created", "live", null), created.ToString());
            var updated = Guid.CreateVersion7();
            await f.PublishRawAsync("puluj.live.incident.changed", IncidentChanged(updated, 1, 2, "updated", "live", null), updated.ToString());
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(replica1.Incidents.Count == 2 && replica2.Incidents.Count == 2), TimeSpan.FromSeconds(20)), "both replicas heard both events");
            Assert.Equal([(1L, 1), (1L, 2)], replica1.Incidents.OrderBy(e => e.Revision).Select(e => (e.Id, e.Revision!.Value)));
            Assert.Equal([(1L, 1), (1L, 2)], replica2.Incidents.OrderBy(e => e.Revision).Select(e => (e.Id, e.Revision!.Value)));
            Assert.Equal(2, await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome = 'completed'"));

            // Redelivery of the same event: inbox duplicate, no second NOTIFY (at-most-once — the reload is the recovery path).
            await f.PublishRawAsync("puluj.live.incident.changed", IncidentChanged(updated, 1, 2, "updated", "live", null), updated.ToString());
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(f.Projection.Duplicates >= 1), TimeSpan.FromSeconds(20)));
            await Task.Delay(500);
            Assert.Equal(2, replica1.Incidents.Count);

            // A track.changed is a receipt-only noop (the writer already notified); an inactive generation never reaches the map; the history lane does.
            var track = Guid.CreateVersion7();
            var trackBody = Encoding.UTF8.GetString(IncidentChanged(track, 5, 1, "created", "live", null)).Replace("\"event_type\":\"incident.changed\"", "\"event_type\":\"track.changed\"");
            await f.PublishRawAsync("puluj.live.track.changed", Encoding.UTF8.GetBytes(trackBody), track.ToString());
            var shadow = Guid.CreateVersion7();
            await f.PublishRawAsync("puluj.live.incident.changed", IncidentChanged(shadow, 9, 1, "created", "live", Guid.Parse("00000000-0000-0000-0000-00000000dead")), shadow.ToString());
            var history = Guid.CreateVersion7();
            await f.PublishRawAsync("puluj.history.incident.changed", IncidentChanged(history, 3, 1, "created", "history", null), history.ToString());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome IS NOT NULL") == 5, TimeSpan.FromSeconds(20)));
            Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome = 'noop' AND reason LIKE 'writer_notifies%'"));
            Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome = 'noop' AND reason LIKE 'not_live%'"));
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(replica1.Incidents.Count == 3 && replica2.Incidents.Count == 3), TimeSpan.FromSeconds(10)), "the history-lane incident reached both replicas, the shadow one did not");
            Assert.DoesNotContain(replica1.Incidents, e => e.Id == 9);

            // LISTEN reconnect: the server drops replica 1's connection → its pump reconnects and yields the resync marker (replica 2 keeps listening).
            var killed = await f.ScalarAsync<long>("SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query ILIKE 'LISTEN %' AND pid <> pg_backend_pid() ORDER BY backend_start LIMIT 1) k");
            Assert.Equal(1, killed);
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(replica1.Reconnects.Count + replica2.Reconnects.Count == 1), TimeSpan.FromSeconds(20)), "one replica reconnected and flagged a resync");
            Assert.True(await WaitListeningAsync(f, 2), "both listening again");

            // Burst: 1 000 revisions of 100 incidents through one projection consumer → every replica hears all of them.
            var before = replica1.Incidents.Count;
            for (var i = 0; i < 1000; i++)
            {
                var id = Guid.CreateVersion7();
                await f.PublishRawAsync("puluj.live.incident.changed", IncidentChanged(id, 100 + i % 100, 1 + i / 100, i < 100 ? "created" : "updated", "live", null), id.ToString());
            }
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(replica1.Incidents.Count == before + 1000 && replica2.Incidents.Count == before + 1000), TimeSpan.FromSeconds(120)),
                $"burst: replica1 {replica1.Incidents.Count - before}, replica2 {replica2.Incidents.Count - before} of 1000");
            Assert.Equal(1000, replica1.Incidents.Where(e => e.Id >= 100).Select(e => (e.Id, e.Revision)).Distinct().Count());
            f.Evidence.Record("P11-R05", new { replicas = 2, notify_per_delivery = 1, redelivery = "inbox duplicate, no NOTIFY", track_changed = "noop writer_notifies", inactive_generation = "noop not_live", history_lane = "pushed", reconnect = "resync marker", burst = 1000 });
        }
        finally
        {
            await f.Projection.StopAsync(None);
        }
    }
}
