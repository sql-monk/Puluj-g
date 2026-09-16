using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// ADR-0004 crash windows assigned to P03 (P03-C01…C10), each asserted on committed database state — outbox,
/// inbox, events, deliveries, attempts, quarantine — never on "a message arrived".
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class CrashTests(MessagingFixture f)
{
    private const string LiveQueue = "puluj.archive.live";
    private const string LiveDlq = "puluj.archive.live.dlq";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task C01_W1b_W2_Bridge_commits_raw_outbox_and_expected_deliveries_atomically_then_relay_publishes()
    {
        await f.ResetAsync();
        var ingest = await f.IngestAsync("c01", "Шахеди на Сумщині курсом на Полтавщину.");
        Assert.True(ingest.IsNew);

        // Committed together, before any relay ran: raw row, unconfirmed outbox row, expected archive delivery, no archive.
        var outboxRow = await f.ScalarAsync<string>("SELECT event_id::text || '|' || event_type || '|' || lane || '|' || routing_key || '|' || (confirmed_at IS NULL)::text FROM messaging.outbox");
        var parts = outboxRow.Split('|');
        Assert.Equal("raw.stored", parts[1]);
        Assert.Equal("live", parts[2]);
        Assert.Equal("puluj.live.raw.stored", parts[3]);
        Assert.Equal("true", parts[4]);
        var eventId = Guid.Parse(parts[0]);
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome IS NULL AND subscription_id = 'archive'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome IS NULL AND subscription_id = 'normalizer'")); // active since v4 (P05)
        Assert.Equal(0, await f.CountAsync("processing.deliveries", "subscription_id NOT IN ('archive', 'normalizer')")); // message-analytics is planned, not expected
        Assert.Equal(0, await f.CountAsync("messaging.events"));
        Assert.Equal(1, await f.CountAsync("processing.runs", "lane = 'live' AND state = 'running'"));
        var envelope = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox"))!;
        Assert.Equal(f.Registry.TopologyVersion, envelope["topology_version"]!.GetValue<int>());
        Assert.Equal(eventId.ToString(), envelope["causation_id"]!.GetValue<string>());
        Assert.Equal("c01", envelope["source_message_key"]!.GetValue<string>());
        Assert.Equal("raw-writer@p03-test", envelope["producer"]!.GetValue<string>());

        // Reconciliation without a relay sees the age of the unconfirmed row.
        await Task.Delay(1100);
        var report = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: false);
        Assert.Equal(1, report.OutboxUnconfirmed);
        Assert.True(report.OutboxOldestAge > TimeSpan.FromSeconds(1));
        Assert.Equal(["archive", "normalizer"], report.OverdueDeliveries.Select(d => d.SubscriptionId).Order());

        var pass = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((1, 1), (pass.Leased, pass.Confirmed));
        Assert.Equal(1, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));

        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events") == 1));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome = 'completed' AND subscription_id = 'archive'") == 1));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        Assert.Equal(eventId, await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.events"));
        Assert.Equal(1, await f.CountAsync("messaging.inbox", "outcome = 'completed'"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "state = 'succeeded'"));
        f.Evidence.Record("P03-C01", new { window = "W1b/W2", raw = 1, outbox_unconfirmed_before_relay = 1, expected_deliveries = new[] { "archive", "normalizer" }, outbox_age_seen_s = report.OutboxOldestAge.TotalSeconds, relay = pass, events = 1, receipt = "completed" });
    }

    [Fact]
    public async Task C02_W2_Relay_crash_before_publish_and_lease_takeover()
    {
        await f.ResetAsync();
        await f.IngestAsync("c02", "БпЛА на Полтавщині у південному напрямку.");
        var eventId = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox");

        // A relay leased the row and died before publishing: nobody else may take it until the lease expires (N3).
        await f.ExecAsync("UPDATE messaging.outbox SET lease_owner = 'dead-relay', lease_until = now() + interval '1.5 seconds', attempts = 1");
        var blocked = await f.Relay.RelayOnceAsync(None);
        Assert.Equal(0, blocked.Leased);
        Assert.Equal(0, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));
        await Task.Delay(1700);
        var after = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((1, 1), (after.Leased, after.Confirmed));
        Assert.Null(await f.ScalarAsync<string?>("SELECT lease_owner FROM messaging.outbox")); // released after confirm
        Assert.Equal(2, await f.ScalarAsync<int>("SELECT attempts FROM messaging.outbox"));

        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events") == 1));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        Assert.Equal(eventId, await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.events"));
        f.Evidence.Record("P03-C02", new { window = "W2 + lease", leased_while_lease_held = blocked.Leased, published_after_lease = after.Confirmed, same_event_id = true, transport_attempts = 2, events = 1 });
    }

    [Fact]
    public async Task C03_W3_W12b_Relay_crash_after_confirm_before_mark_republishes_and_inbox_absorbs()
    {
        await f.ResetAsync();
        await f.IngestAsync("c03", "Ракети курсом на Київ.");
        var first = await f.Relay.RelayOnceAsync(None, markConfirmed: false); // confirm received, mark never written
        Assert.Equal((1, 1), (first.Leased, first.Confirmed));
        Assert.Equal(0, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));
        Assert.Equal(1u, (await f.QueueAsync(LiveQueue)).Messages);

        await Task.Delay(f.Options.Relay.Lease + TimeSpan.FromMilliseconds(300)); // the dead relay's lease expires
        var second = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((1, 1), (second.Leased, second.Confirmed));
        Assert.Equal(2u, (await f.QueueAsync(LiveQueue)).Messages); // same event id twice in the queue

        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(f.Archive.Delivered >= 2 && f.Archive.Duplicates == 1)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(LiveQueue)).Messages == 0));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("messaging.events"));
        Assert.Equal(1, await f.CountAsync("messaging.inbox"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome = 'completed'"));
        Assert.Equal(1, f.Archive.Duplicates);
        f.Evidence.Record("P03-C03", new { window = "W3/W12b", publishes = 2, deliveries_seen = f.Archive.Delivered, inbox_rows = 1, events = 1, duplicates_suppressed = f.Archive.Duplicates });
    }

    [Fact]
    public async Task C04_W6a_Attempts_exhausted_quarantine_survives_crash_before_receipt_and_before_nack()
    {
        await f.ResetAsync();
        await f.IngestAsync("c04", "Вибухи у Харкові.");
        var eventId = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox");
        await f.Relay.RelayOnceAsync(None);

        var handler = new FaultyArchiveHandler(new ArchiveHandler(), int.MaxValue);
        var consumer = f.NewConsumer(handler, "archive@faulty");
        consumer.MaxAttemptsOverride = 3;
        consumer.RestartDelay = TimeSpan.FromMilliseconds(300);
        var hooks = new OnceHooks().Arm(nameof(OnceHooks.BeforeQuarantineCommit), nameof(OnceHooks.AfterQuarantineCommitBeforeNack));
        consumer.Hooks = hooks;
        await consumer.StartAsync(None);
        try
        {
            // 3 failed attempts → 4th delivery crashes before the quarantine receipt (W6a-1) → redelivery sees attempts ≥ max,
            // writes the receipt without a new attempt, crashes before the nack (W6a-2) → redelivery: inbox says quarantined → nack → DLQ.
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(LiveDlq)).Messages == 1, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await consumer.StopAsync(None);
        }
        Assert.Equal([nameof(OnceHooks.BeforeQuarantineCommit), nameof(OnceHooks.AfterQuarantineCommitBeforeNack)], hooks.Fired);
        Assert.Equal(3, await f.CountAsync("processing.attempts", "state = 'failed'"));
        Assert.Equal(3, await f.CountAsync("processing.attempts"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome = 'quarantined' AND reason = 'attempts_exhausted'"));
        Assert.Equal(1, await f.CountAsync("processing.quarantine", "resolved_at IS NULL AND reason = 'attempts_exhausted'"));
        Assert.Equal(1, await f.CountAsync("messaging.inbox", "outcome = 'quarantined'"));
        Assert.Equal(0, await f.CountAsync("messaging.events"));
        Assert.Equal(0u, (await f.QueueAsync(LiveQueue)).Messages);

        // The DLQ consumer finds the receipt already committed: it only ACKs, no second quarantine.
        await f.Dlq.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(LiveDlq)).Messages == 0));
        }
        finally
        {
            await f.Dlq.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P03-C04", new { window = "W6a-1/W6a-2", max_attempts = 3, attempts_failed = 3, crashes = hooks.Fired, deliveries_seen = consumer.Delivered, receipt = "quarantined", quarantine_rows = 1, dlq_after_consumer = 0, events = 0, event_id = eventId });
    }

    [Fact]
    public async Task C05_W6b_Admin_retry_after_outbox_cleanup_without_events_row()
    {
        await f.ResetAsync();
        await f.IngestAsync("c05", "Дрони над Одещиною.");
        var eventId = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox");
        await f.Relay.RelayOnceAsync(None);

        var faulty = f.NewConsumer(new FaultyArchiveHandler(new ArchiveHandler(), int.MaxValue), "archive@faulty");
        faulty.MaxAttemptsOverride = 2;
        await faulty.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.quarantine", "resolved_at IS NULL") == 1, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await faulty.StopAsync(None);
        }
        var quarantineId = await f.ScalarAsync<long>("SELECT quarantine_id FROM processing.quarantine");
        var lastAttempt = await f.ScalarAsync<long>("SELECT max(attempt_id) FROM processing.attempts");

        // The outbox row is gone (review B2) and the archive never got the event: the quarantine row is the only copy.
        await f.ExecAsync("DELETE FROM messaging.outbox");
        Assert.Equal(0, await f.CountAsync("messaging.events"));

        var outboxId = await f.Admin.RetryAsync(quarantineId, "operator:test", None);
        Assert.Equal(1, await f.CountAsync("messaging.outbox", "target_queue = 'puluj.archive.live' AND confirmed_at IS NULL"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome IS NULL AND actor = 'operator:test'"));
        Assert.Equal(1, await f.CountAsync("processing.quarantine", "resolved_at IS NOT NULL AND resolution = 'retried' AND retry_outbox_id = " + outboxId));
        Assert.Equal(0, await f.CountAsync("messaging.inbox"));
        Assert.Equal(1, await f.CountAsync("messaging.control_audit", "action = 'retry' AND subscription_id = 'archive' AND lane = 'live' AND actor = 'operator:test' AND (details->>'quarantineId')::bigint = " + quarantineId)); // P13: one audit source

        var pass = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((1, 1), (pass.Leased, pass.Confirmed));
        await f.Archive.StartAsync(None); // the healthy handler this time
        await f.Dlq.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome = 'completed'") == 1));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(LiveDlq)).Messages == 0)); // stale DLQ copy: ACKed, no new quarantine
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await f.Dlq.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("messaging.events", $"event_id = '{eventId}'"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", $"state = 'succeeded' AND retry_of_attempt_id = {lastAttempt} AND retry_reason = 'admin_retry'"));
        Assert.Equal(2, await f.CountAsync("processing.attempts", "state = 'superseded'"));
        Assert.Equal(1, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P03-C05", new { window = "W6b", max_attempts = 2, outbox_deleted_before_retry = true, events_before_retry = 0, retry_outbox_id = outboxId, retry_of_attempt_id = lastAttempt, receipt = "completed", quarantine_resolution = "retried", quarantine_rows = 1 });
    }

    [Fact]
    public async Task C06_W9a_Expected_set_is_fixed_by_the_topology_version_of_the_event()
    {
        await f.ResetAsync();
        // An event published under topology v1 (no subscription was active): its expected set is empty, recorded at publish time.
        await f.ExecAsync("INSERT INTO messaging.topology_versions (topology_version, hash, applied_at, applied_by) VALUES (1, 'v1-hash', now() - interval '1 day', 'p02')");
        await f.ExecAsync("INSERT INTO messaging.subscriptions (subscription_id, topology_version, required, status, bindings, lanes, queue_policy, updated_at) VALUES ('archive', 1, true, 'planned', '[\"raw.stored\"]', '[\"live\"]', 'required', now())");
        var v1Event = Guid.CreateVersion7();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "fixtures", "valid", "raw.stored.json")))!.AsObject();
        fixture["event_id"] = v1Event.ToString();
        fixture["topology_version"] = 1;
        await f.ExecAsync("INSERT INTO messaging.outbox (event_id, event_type, lane, routing_key, replay_source, envelope, created_at, attempts, next_attempt_at, confirmed_at) VALUES (@e, 'raw.stored', 'live', 'puluj.live.raw.stored', true, @env::jsonb, now() - interval '1 hour', 1, now(), now() - interval '1 hour')",
            ("e", v1Event), ("env", fixture.ToJsonString()));
        // A v2 event: archive is active now and expected.
        await f.IngestAsync("c06", "Тривога у Львові.");
        var v2Event = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox WHERE event_id <> @e", ("e", v1Event));

        var report = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: false);
        Assert.Equal([v2Event], report.OverdueDeliveries.Select(d => d.EventId).Distinct()); // one row per active subscription (archive, normalizer)
        Assert.Equal(0, await f.CountAsync("processing.deliveries", $"event_id = '{v1Event}'"));
        Assert.Equal(f.Registry.TopologyVersion, await f.ScalarAsync<int>("SELECT topology_version FROM processing.deliveries WHERE event_id = @e", ("e", v2Event)));
        Assert.Equal(1, await f.CountAsync("messaging.subscriptions", "subscription_id = 'archive' AND topology_version = 1 AND status = 'planned'"));
        Assert.Equal(1, await f.CountAsync("messaging.subscriptions", "subscription_id = 'archive' AND topology_version = " + f.Registry.TopologyVersion + " AND status = 'active'"));
        f.Evidence.Record("P03-C06", new { window = "W9a", v1_event_expected = 0, v2_event_expected = new[] { "archive" }, overdue_reported = report.OverdueDeliveries.Count, registry_versions = new[] { 1, f.Registry.TopologyVersion } });
    }

    [Fact]
    public async Task C07_W9b_Paused_required_subscription_stays_expected_until_audited_waiver()
    {
        await f.ResetAsync();
        await f.Admin.SetStatusAsync("archive", "paused", "maintenance window", "operator:test", None);
        for (var i = 0; i < 3; i++)
        {
            await f.IngestAsync($"c07-{i}", $"Повідомлення {i}");
        }
        await f.Relay.RelayOnceAsync(None);
        // Paused = still interested: expected rows exist, the queue keeps the backlog, nothing completes.
        Assert.Equal(3, await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL"));
        Assert.Equal(3u, (await f.QueueAsync(LiveQueue)).Messages);
        var before = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: false);
        Assert.Equal(3, before.OverdueDeliveries.Count(d => d.SubscriptionId == "archive"));

        var waived = await f.Admin.WaiveAsync("archive", "consumer retired for the maintenance window; evidence kept in raw_messages", "operator:test", null, None);
        Assert.Equal(3, waived);
        Assert.Equal(3, await f.CountAsync("processing.deliveries", "outcome = 'waived' AND actor = 'operator:test' AND reason LIKE 'consumer retired%'"));
        var after = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: false);
        Assert.DoesNotContain(after.OverdueDeliveries, d => d.SubscriptionId == "archive"); // the normalizer's rows (v4) are still expected
        var waiver = await f.ScalarAsync<string>("SELECT waiver::text FROM messaging.subscriptions WHERE subscription_id = 'archive' AND topology_version = " + f.Registry.TopologyVersion);
        Assert.Contains("operator:test", waiver);
        Assert.Equal(1, await f.CountAsync("messaging.control_audit", "action = 'waive' AND subscription_id = 'archive' AND actor = 'operator:test' AND (details->>'waived')::int = 3")); // P13
        Assert.Equal("paused", await f.ScalarAsync<string>("SELECT status FROM messaging.subscriptions WHERE subscription_id = 'archive' AND topology_version = " + f.Registry.TopologyVersion));

        await f.Admin.SetStatusAsync("archive", "active", "maintenance over", "operator:test", None);
        Assert.Equal("active", await f.ScalarAsync<string>("SELECT status FROM messaging.subscriptions WHERE subscription_id = 'archive' AND topology_version = " + f.Registry.TopologyVersion));
        f.Evidence.Record("P03-C07", new { window = "W9b", paused_expected = 3, backlog_kept = 3, waived, overdue_after_waiver_archive = after.OverdueDeliveries.Count(d => d.SubscriptionId == "archive"), waiver_audit = waiver });
    }

    [Fact]
    public async Task C08_W11_Missing_binding_is_returned_not_confirmed_and_repaired_by_redeclare()
    {
        await f.ResetAsync();
        var connection = await f.Broker.GetAsync(None);
        await using (var channel = await connection.CreateChannelAsync())
        {
            // Every bound queue must go: `mandatory` only says "at least one queue" (P02 C08), so with the normalizer still
            // bound (v4) the publish would be confirmed and only the archive would silently miss the event.
            await channel.QueueUnbindAsync(LiveQueue, f.Registry.ExchangeName, "puluj.live.raw.stored");
            await channel.QueueUnbindAsync("puluj.normalizer.live", f.Registry.ExchangeName, "puluj.live.raw.stored");
        }
        await f.IngestAsync("c08", "Пуск ракет з Криму.");
        var pass = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((1, 0, 1), (pass.Leased, pass.Confirmed, pass.Unroutable));
        var error = await f.ScalarAsync<string>("SELECT last_error FROM messaging.outbox");
        Assert.Contains("basic.return", error);
        Assert.Equal(0, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));
        Assert.Equal(0u, (await f.QueueAsync(LiveQueue)).Messages);

        // Reconciliation re-declares (idempotent) and the binding is back; the relay retries after the unroutable pause.
        var report = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: true);
        Assert.Empty(report.DeclareFailed);
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.Relay.RelayOnceAsync(None)).Confirmed == 1, TimeSpan.FromSeconds(10), 300));
        Assert.Equal(1, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events") == 1));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        f.Evidence.Record("P03-C08", new { window = "W11", unroutable_first_pass = pass.Unroutable, last_error = error, confirmed_after_redeclare = 1, events = 1 });
    }

    [Fact]
    public async Task C09_W12a_Transient_failure_before_commit_is_retried_without_duplicate_effect()
    {
        await f.ResetAsync();
        await f.IngestAsync("c09", "Балістика на Дніпро.");
        await f.Relay.RelayOnceAsync(None);
        var handler = new FaultyArchiveHandler(new ArchiveHandler(), 1);
        var consumer = f.NewConsumer(handler, "archive@flaky");
        await consumer.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome = 'completed'") == 1));
        }
        finally
        {
            await consumer.StopAsync(None);
        }
        Assert.Equal(1, handler.Failed);
        Assert.Equal(1, consumer.Requeued);
        Assert.Equal(1, await f.CountAsync("processing.attempts", "state = 'failed' AND error LIKE '%DB outage%'"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "state = 'succeeded'"));
        Assert.Equal(1, await f.CountAsync("messaging.events"));
        Assert.Equal(1, await f.CountAsync("messaging.inbox", "outcome = 'completed'"));
        Assert.Equal(0, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P03-C09", new { window = "W12a", transient_failures = handler.Failed, requeued = consumer.Requeued, attempts = new { failed = 1, succeeded = 1 }, events = 1 });
    }

    [Fact]
    public async Task C10_W14a_Duplicate_deliveries_are_absorbed_by_the_inbox()
    {
        await f.ResetAsync();
        await f.IngestAsync("c10", "Шахеди на Чернігівщині.");
        var body = Encoding.UTF8.GetBytes(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox"));
        var eventId = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox");
        for (var i = 0; i < 3; i++)
        {
            await f.PublishRawAsync("puluj.live.raw.stored", body, eventId.ToString());
        }
        Assert.Equal(3u, (await f.QueueAsync(LiveQueue)).Messages);
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(LiveQueue)).Messages == 0 && f.Archive.Delivered >= 3 && f.Archive.Duplicates == 2));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("messaging.inbox"));
        Assert.Equal(1, await f.CountAsync("messaging.events"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "outcome = 'completed'"));
        Assert.Equal(2, f.Archive.Duplicates);
        Assert.Equal(1, await f.CountAsync("processing.attempts"));
        f.Evidence.Record("P03-C10", new { window = "W14a", published = 3, delivered = f.Archive.Delivered, duplicates_suppressed = f.Archive.Duplicates, inbox = 1, events = 1, attempts = 1 });
    }
}
