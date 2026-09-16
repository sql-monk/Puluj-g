using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities.Messaging;
using Puluj.Infrastructure.Messaging.Ops;
using Puluj.Infrastructure.Settings;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P13 (ADR-0012): runtime controls with an exact scope — pause/resume/drain of one lane of one subscription — and
/// the ops snapshot/alarms over real receipts. Every assert is on the broker (consumer count of the queue) or on
/// committed rows (`processing.deliveries`, `messaging.subscription_lanes`, `messaging.control_audit`).
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class OpsControlTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Subscription = "raw-writer";

    private string Queue(string lane) => f.Registry.QueueName(Subscription, lane);

    private async Task PublishAsync(string id, bool live)
    {
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message(id, $"Тест {id}: дрони над Сумщиною."), source, "test", null, live, None);
    }

    [Fact]
    public async Task O01_Pause_of_one_lane_cancels_only_that_consumer_and_resume_drains_the_backlog()
    {
        await f.ResetAsync();
        await f.Admin.SetLaneStateAsync(Subscription, "history", SubscriptionLane.Paused, "operator:test", "backfill window: history waits", None);
        for (var i = 0; i < 3; i++)
        {
            await PublishAsync($"o01-h{i}", live: false);
        }
        await PublishAsync("o01-live", live: true);
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        try
        {
            // The live lane is served, the paused history lane is not even subscribed (state read before the first basic.consume).
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 1, TimeSpan.FromSeconds(20)), "live message stored");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(Queue("history"))).Messages == 3, TimeSpan.FromSeconds(20)), "history queue holds the backlog");
            Assert.Equal(0u, (await f.QueueAsync(Queue("history"))).Consumers);
            Assert.Equal(1u, (await f.QueueAsync(Queue("live"))).Consumers);
            Assert.Equal(3, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND lane = 'history' AND outcome IS NULL"));
            Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND lane = 'live' AND outcome = 'completed'"));
            var lanes = f.RawWriter.Lanes();
            Assert.Equal(SubscriptionLane.Paused, lanes.Single(l => l.Lane == "history").State);
            Assert.False(lanes.Single(l => l.Lane == "history").Consuming);
            Assert.True(lanes.Single(l => l.Lane == "live").Consuming);
            Assert.Equal($"raw-writer@p03-test:live", lanes.Single(l => l.Lane == "live").ConsumerTag);

            // Pause while consuming: the consumer tag of the live lane is cancelled on the next poll; a new live message waits.
            await f.Admin.SetLaneStateAsync(Subscription, "live", SubscriptionLane.Paused, "operator:test", "incident: stop live intake", None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(Queue("live"))).Consumers == 0, TimeSpan.FromSeconds(10)), "live consumer cancelled");
            await PublishAsync("o01-live-2", live: true);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(Queue("live"))).Messages == 1, TimeSpan.FromSeconds(10)));
            await Task.Delay(500);
            Assert.Equal(1, await f.CountAsync("raw_messages"));

            // Resume both: everything drains; the same channel is reused for the live lane.
            await f.Admin.SetLaneStateAsync(Subscription, "history", SubscriptionLane.Active, "operator:test", "backfill window over", None);
            await f.Admin.SetLaneStateAsync(Subscription, "live", SubscriptionLane.Active, "operator:test", "incident over", None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 5, TimeSpan.FromSeconds(20)), $"raw rows: {await f.CountAsync("raw_messages")}");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(10)));
            Assert.Equal(1u, (await f.QueueAsync(Queue("history"))).Consumers);
            Assert.Equal(1u, (await f.QueueAsync(Queue("live"))).Consumers);
            Assert.All(f.RawWriter.Lanes(), l => Assert.True(l.Consuming && l.State == SubscriptionLane.Active));
        }
        finally
        {
            await f.RawWriter.StopAsync(None);
            await f.Relay.StopAsync(None);
            await f.ExecAsync("UPDATE messaging.subscription_lanes SET state = 'active' WHERE subscription_id = 'raw-writer'"); // never leave a lane paused for a later test (review B4)
        }
        var audit = await f.ScalarAsync<string>("SELECT string_agg(action || ':' || lane || ':' || actor, ',' ORDER BY audit_id) FROM messaging.control_audit");
        Assert.Equal("pause:history:operator:test,pause:live:operator:test,resume:history:operator:test,resume:live:operator:test", audit);
        f.Evidence.Record("P13-O01", new { scope = "raw-writer/history then raw-writer/live", paused_consumers = 0, live_consumers_during_history_pause = 1, backlog = 3, drained_after_resume = 5, audit });
    }

    [Fact]
    public async Task O02_Drain_keeps_consuming_until_the_queue_is_empty_then_pauses_itself()
    {
        await f.ResetAsync();
        for (var i = 0; i < 4; i++)
        {
            await PublishAsync($"o02-h{i}", live: false);
        }
        // The backlog is in the broker before the drain starts: an empty queue drains at once (that is the point), which is not what this test measures.
        await f.Relay.StartAsync(None);
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(Queue("history"))).Messages == 4, TimeSpan.FromSeconds(20)), "backlog published");
        await f.Admin.SetLaneStateAsync(Subscription, "history", SubscriptionLane.Draining, "operator:test", "finish the backfill, then stop", None);
        await f.RawWriter.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 4, TimeSpan.FromSeconds(20)), "draining lane still consumes");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.ScalarAsync<string>("SELECT state FROM messaging.subscription_lanes WHERE subscription_id = 'raw-writer' AND lane = 'history'") == SubscriptionLane.Paused, TimeSpan.FromSeconds(10)), "drained → paused by the consumer");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(Queue("history"))).Consumers == 0, TimeSpan.FromSeconds(10)));
            Assert.Equal("system|drained", await f.ScalarAsync<string>("SELECT actor || '|' || reason FROM messaging.subscription_lanes WHERE subscription_id = 'raw-writer' AND lane = 'history'"));
            Assert.Equal(1, await f.CountAsync("messaging.control_audit", "action = 'drained' AND actor = 'system' AND subscription_id = 'raw-writer' AND lane = 'history'"));
            Assert.Equal(1u, (await f.QueueAsync(Queue("live"))).Consumers); // scope: only history
            Assert.Equal(SubscriptionLane.Paused, f.RawWriter.Lanes().Single(l => l.Lane == "history").State);
        }
        finally
        {
            await f.RawWriter.StopAsync(None);
            await f.Relay.StopAsync(None);
            await f.Admin.SetLaneStateAsync(Subscription, "history", SubscriptionLane.Active, "operator:test", "cleanup", None);
        }
        f.Evidence.Record("P13-O02", new { scope = "raw-writer/history", backlog = 4, stored = 4, final_state = "paused", actor = "system", audit = "drained" });
    }

    [Fact]
    public async Task O03_Ops_snapshot_counts_match_the_receipts_and_alarms_name_the_scope()
    {
        await f.ResetAsync();
        var settings = f.Services.GetRequiredService<SettingsStore>();
        var snapshots = f.Services.GetRequiredService<OpsSnapshotService>();
        for (var i = 0; i < 3; i++)
        {
            await PublishAsync($"o03-{i}", live: true);
        }
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 3 && await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(30)));
            await f.Relay.RelayOnceAsync(None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id IN ('archive', 'raw-writer') AND outcome IS NULL") == 0, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await f.RawWriter.StopAsync(None);
            await f.Relay.StopAsync(None);
        }
        // A stuck worker: stale heartbeat (5 min) with a running attempt of the parser; a quarantined delivery of the normalizer.
        var stored = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox WHERE event_type = 'raw.stored' ORDER BY outbox_id LIMIT 1");
        await settings.SetStatusAsync("Worker:ghost:Heartbeat", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"), None);
        try
        {
            await f.ExecAsync("INSERT INTO processing.attempts (subscription_id, event_id, job_key, worker, state, fencing_token, started_at) VALUES ('parser', @e, 'parser:' || @e::text, 'parser@ghost', 'running', 0, now() - interval '10 minutes')", ("e", stored));
            await f.ExecAsync("INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, lane) VALUES (@e, 'parser', @v, now() - interval '10 minutes', 'live') ON CONFLICT DO NOTHING", ("e", stored), ("v", f.Registry.TopologyVersion));
            await f.ExecAsync("INSERT INTO processing.quarantine (subscription_id, event_id, lane, reason, error, envelope, quarantined_at) VALUES ('normalizer', @e, 'live', 'invalid_payload', 'boom', '{}'::jsonb, now())", ("e", stored));
            var s = await snapshots.SnapshotAsync(None, fresh: true);

            var rawLive = s.Subscriptions.Single(x => x.Subscription == "raw-writer" && x.Lane == "live");
            Assert.Equal(3, rawLive.CompletedHour);
            Assert.Equal(0, rawLive.Pending);
            Assert.Equal(await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND lane = 'live' AND outcome = 'completed'"), rawLive.CompletedHour);
            Assert.NotNull(rawLive.WaitP50Ms);
            Assert.NotNull(rawLive.ProcessingP95Ms);
            Assert.Equal("db", rawLive.Source);
            var archiveLive = s.Subscriptions.Single(x => x.Subscription == "archive" && x.Lane == "live");
            Assert.Equal(await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND lane = 'live' AND outcome IN ('completed', 'noop')"), archiveLive.CompletedHour + archiveLive.NoopHour);
            var normalizer = s.Subscriptions.Single(x => x.Subscription == "normalizer" && x.Lane == "live");
            Assert.Equal(3, normalizer.Pending); // expected, never consumed in this test
            Assert.Equal(await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND outcome IS NULL"), normalizer.Pending);
            Assert.True(normalizer.OldestPendingAgeSeconds > 0);
            Assert.Equal(1, normalizer.Quarantined);
            var parser = s.Subscriptions.Single(x => x.Subscription == "parser" && x.Lane == "live");
            Assert.Equal(1, parser.InFlight);
            Assert.True(parser.OldestRunningAttemptAgeSeconds > 500);
            Assert.Equal(3, s.Roots.ReceivedHour);
            Assert.True(s.Roots.Pending >= 1);
            Assert.Equal(1, s.Roots.NeedsAttention);

            var ghost = s.Workers.Single(w => w.Name == "ghost");
            Assert.True(ghost.Stale);
            Assert.True(ghost.Stuck);
            Assert.Equal(1, ghost.RunningAttempts);
            var codes = s.Alarms.Select(a => $"{a.Code}@{a.Scope}").ToList();
            Assert.Contains("stale_heartbeat_with_jobs@worker:ghost", codes);
            Assert.Contains("dlq@normalizer/live", codes);
            Assert.Contains("required_consumer_missing@normalizer/live", codes);
            Assert.Contains("inflight_stuck@parser/live", codes);
            Assert.DoesNotContain(codes, c => c.StartsWith("lane_paused", StringComparison.Ordinal));

            // A paused lane is never silent and never an error: info with actor and reason.
            await f.Admin.SetLaneStateAsync("normalizer", "live", SubscriptionLane.Paused, "operator:test", "waiting for the parser fix", None);
            s = await snapshots.SnapshotAsync(None, fresh: true);
            var paused = s.Alarms.Single(a => a.Code == "lane_paused" && a.Scope == "normalizer/live");
            Assert.Equal(AlarmRules.Info, paused.Severity);
            Assert.Contains("operator:test", paused.Message);
            Assert.Contains("waiting for the parser fix", paused.Message);
            Assert.DoesNotContain(s.Alarms, a => a.Code == "required_consumer_missing" && a.Scope == "normalizer/live");
            f.Evidence.Record("P13-O03", new { rawLive.CompletedHour, normalizerPending = normalizer.Pending, alarms = codes, paused_alarm = paused });
        }
        finally
        {
            await settings.SetStatusAsync("Worker:ghost:Heartbeat", null, None);
            await f.Admin.SetLaneStateAsync("normalizer", "live", SubscriptionLane.Active, "operator:test", "cleanup", None);
        }
    }

    [Fact]
    public async Task O04_Lifecycle_card_shows_events_receipts_attempts_and_who_is_still_waiting()
    {
        await f.ResetAsync();
        var explorer = f.Services.GetRequiredService<MessageExplorer>();
        await PublishAsync("o04", live: true);
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 1, TimeSpan.FromSeconds(20)));
            await f.Relay.RelayOnceAsync(None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events", "event_type = 'raw.stored'") == 1 && await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await f.RawWriter.StopAsync(None);
            await f.Relay.StopAsync(None);
        }
        var rawId = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages");
        var found = await explorer.SearchAsync("дрони", null, 24, 10, None);
        Assert.Single(found);
        Assert.Equal(rawId, found[0].RawMessageId);
        Assert.Equal("o04", found[0].SourceMessageId);
        Assert.Equal(rawId, (await explorer.SearchAsync(rawId.ToString(), null, 24, 10, None)).Single().RawMessageId);
        Assert.Empty(await explorer.SearchAsync("nothing-like-this", null, 24, 10, None));

        var card = await explorer.LifecycleAsync(rawId, None);
        Assert.NotNull(card);
        Assert.Equal("o04", card.SourceMessageId);
        Assert.Contains("дрони", card.Text!);
        var stored = card.Events.Single(e => e.EventType == "raw.stored");
        Assert.Contains(stored.Deliveries, d => d.Subscription == "archive" && d.Outcome is "completed" or "noop" && d.Attempts.Count == 1 && d.Attempts[0].State == "succeeded");
        Assert.Contains(stored.Deliveries, d => d.Subscription == "normalizer" && d.Outcome is null && d.Attempts.Count == 0);
        Assert.Contains("normalizer/live", card.Summary.Waiting);
        Assert.Contains("archive/live", card.Summary.Completed);
        Assert.Equal("in_progress", card.Summary.Completion);
        Assert.Empty(card.Quarantine);

        // A failed branch: the quarantine row of that event appears on the card with its error, and the root needs attention.
        await f.ExecAsync("INSERT INTO processing.quarantine (subscription_id, event_id, lane, reason, error, envelope, quarantined_at) VALUES ('normalizer', @e, 'live', 'attempts_exhausted', 'NullReferenceException: boom', '{}'::jsonb, now())", ("e", stored.EventId));
        await f.ExecAsync("UPDATE processing.deliveries SET outcome = 'quarantined', completed_at = now(), reason = 'attempts_exhausted' WHERE event_id = @e AND subscription_id = 'normalizer'", ("e", stored.EventId));
        card = await explorer.LifecycleAsync(rawId, None);
        Assert.NotNull(card);
        Assert.Equal("needs_attention", card.Summary.Completion);
        Assert.Contains("normalizer/live", card.Summary.Failed);
        Assert.Equal("NullReferenceException: boom", card.Quarantine.Single().Error);

        // Waiving the quarantined delivery (what POST /quarantine/{id}/waive does) closes the receipt too: the root is complete, not "needs attention".
        var waived = await f.Admin.WaiveAsync("normalizer", "parser fix will not help this one", "operator:test", [stored.EventId], None);
        Assert.Equal(1, waived);
        card = await explorer.LifecycleAsync(rawId, None);
        Assert.NotNull(card);
        Assert.Equal("completed", card.Summary.Completion);
        Assert.Empty(card.Summary.Failed);
        Assert.Contains("normalizer/live", card.Summary.Completed);
        Assert.Equal("waived", card.Quarantine.Single().Resolution);
        Assert.Equal("waived", card.Events.Single(e => e.EventType == "raw.stored").Deliveries.Single(d => d.Subscription == "normalizer").Outcome);
        Assert.Null(await explorer.LifecycleAsync(rawId + 1000, None));
        f.Evidence.Record("P13-O04", new { rawId, events = card.Events.Select(e => e.EventType), card.Summary });
    }
}
