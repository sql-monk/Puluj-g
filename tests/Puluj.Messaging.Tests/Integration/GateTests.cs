using System.Text.Json.Nodes;
using Npgsql;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>Issue #4 acceptance beyond the crash windows: inbox dedup across replicas, archive independent of the outbox, reconciliation audit, bridge atomicity.</summary>
[Collection(MessagingCollection.Name)]
public sealed class GateTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task G01_Two_replicas_share_200_deliveries_without_duplicate_effects()
    {
        await f.ResetAsync();
        const int n = 200;
        for (var i = 0; i < n; i++)
        {
            await f.IngestAsync($"g01-{i}", $"Повідомлення {i} про БпЛА.");
        }
        Assert.Equal(n, await f.CountAsync("messaging.outbox", "confirmed_at IS NULL"));
        var pass = await f.Relay.RelayOnceAsync(None);
        Assert.Equal((n, n), (pass.Leased, pass.Confirmed));

        var replica = f.NewConsumer();
        await f.Archive.StartAsync(None);
        await replica.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome = 'completed'") == n, TimeSpan.FromSeconds(60)));
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await replica.StopAsync(None);
        }
        Assert.Equal(n, await f.CountAsync("messaging.events"));
        Assert.Equal(n, await f.CountAsync("messaging.inbox"));
        Assert.Equal(n, f.Archive.Delivered + replica.Delivered);
        Assert.True(f.Archive.Delivered > 0 && replica.Delivered > 0, $"work not shared: {f.Archive.Delivered}/{replica.Delivered}");
        Assert.Equal(0, f.Archive.Duplicates + replica.Duplicates);
        Assert.Equal(n, await f.CountAsync("processing.attempts", "state = 'succeeded'"));
        f.Evidence.Record("G01-competing-consumers", new { published = n, replica_1 = f.Archive.Delivered, replica_2 = replica.Delivered, events = n, duplicates = 0, batch_confirm = pass });
    }

    [Fact]
    public async Task G02_Outbox_cleanup_waits_for_the_archive_receipt_and_the_archive_stays_readable()
    {
        await f.ResetAsync();
        await f.IngestAsync("g02-a", "Перше повідомлення.");
        await f.IngestAsync("g02-b", "Друге повідомлення.");
        await f.Relay.RelayOnceAsync(None);
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events") == 2));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        // A third event is confirmed but never archived (consumer stopped).
        await f.IngestAsync("g02-c", "Третє повідомлення.");
        await f.Relay.RelayOnceAsync(None);
        Assert.Equal(3, await f.CountAsync("messaging.outbox", "confirmed_at IS NOT NULL"));
        await f.ExecAsync("UPDATE messaging.outbox SET confirmed_at = now() - interval '1 day'"); // past the grace period

        var report = await f.Reconciliation.RunOnceAsync(None, cleanup: true, redeclare: false);
        Assert.Equal(2, report.OutboxDeleted);
        Assert.Equal(1, await f.CountAsync("messaging.outbox"));
        var kept = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox");
        Assert.Equal(0, await f.CountAsync("messaging.events", $"event_id = '{kept}'"));

        // The archive is the replay source: full envelopes, independent of the cleaned outbox.
        Assert.Equal(2, await f.CountAsync("messaging.events"));
        var archived = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.events ORDER BY published_at LIMIT 1"))!;
        Assert.Equal("raw.stored", archived["event_type"]!.GetValue<string>());
        Assert.Equal("g02-a", archived["source_message_key"]!.GetValue<string>());
        Assert.NotNull(archived["payload"]!["content_hash"]);
        Assert.Equal(2, await f.CountAsync("messaging.events", "raw_message_id IS NOT NULL AND correlation_id IS NOT NULL"));

        // Inbox retention: completed rows older than the window go, quarantined ones stay.
        await f.ExecAsync("UPDATE messaging.inbox SET completed_at = now() - interval '1 day'");
        await f.ExecAsync("INSERT INTO messaging.inbox (subscription_id, event_id, received_at, completed_at, outcome) VALUES ('archive', @e, now() - interval '2 days', now() - interval '2 days', 'quarantined')", ("e", Guid.CreateVersion7()));
        var second = await f.Reconciliation.RunOnceAsync(None, cleanup: true, redeclare: false);
        Assert.Equal(2, second.InboxDeleted);
        Assert.Equal(1, await f.CountAsync("messaging.inbox", "outcome = 'quarantined'"));
        f.Evidence.Record("G02-archive-independent-of-outbox", new { confirmed = 3, archived = 2, outbox_deleted = report.OutboxDeleted, outbox_kept_unarchived = 1, inbox_deleted = second.InboxDeleted });
    }

    [Fact]
    public async Task G03_Reconciliation_reports_overdue_unknown_and_quarantine()
    {
        await f.ResetAsync();
        await f.IngestAsync("g03", "Повідомлення без consumer.");
        await f.ExecAsync("INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, outcome, completed_at) VALUES (@e, 'ghost-subscription', 2, now(), 'completed', now())", ("e", Guid.CreateVersion7()));
        await f.ExecAsync("INSERT INTO processing.quarantine (subscription_id, event_id, lane, reason, envelope, quarantined_at) VALUES ('archive', @e, 'live', 'invalid_payload', '{}'::jsonb, now())", ("e", Guid.CreateVersion7()));
        await Task.Delay(1100);
        var report = await f.Reconciliation.RunOnceAsync(None, cleanup: false, redeclare: true);
        Assert.Equal(["archive", "normalizer"], report.OverdueDeliveries.Select(d => d.SubscriptionId).Order()); // active subscriptions of raw.stored (v4)
        Assert.Equal(["ghost-subscription"], report.UnknownSubscriptions);
        Assert.Equal(1, report.QuarantineOpen);
        Assert.Equal(1, report.OutboxUnconfirmed);
        Assert.True(report.OutboxOldestAge > TimeSpan.FromSeconds(1));
        Assert.Empty(report.DeclareFailed);
        Assert.Empty(await f.Declarer.MissingRequiredQueuesAsync(None));
        f.Evidence.Record("G03-reconciliation", new { overdue = report.OverdueDeliveries.Count, unknown = report.UnknownSubscriptions, quarantine_open = report.QuarantineOpen, outbox_unconfirmed = report.OutboxUnconfirmed });
    }

    [Fact]
    public async Task G04_Bridge_is_atomic_and_duplicates_publish_nothing()
    {
        await f.ResetAsync();
        // Duplicate (source, source_message_id): no raw, no outbox row, no expected delivery.
        var first = await f.IngestAsync("g04", "Той самий пост.");
        var duplicate = await f.IngestAsync("g04", "Той самий пост.");
        Assert.True(first.IsNew);
        Assert.False(duplicate.IsNew);
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        Assert.Equal(1, await f.CountAsync("messaging.outbox"));

        // The expected-deliveries insert fails → the raw row must not exist either (one transaction).
        await f.ExecAsync("""
            CREATE OR REPLACE FUNCTION processing.p03_test_fail() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated outbox failure'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER p03_test_fail BEFORE INSERT ON processing.deliveries FOR EACH ROW EXECUTE FUNCTION processing.p03_test_fail();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => f.IngestAsync("g04-fail", "Пост, який не має зберегтися."));
        }
        finally
        {
            await f.ExecAsync("DROP TRIGGER p03_test_fail ON processing.deliveries; DROP FUNCTION processing.p03_test_fail();");
        }
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        Assert.Equal(1, await f.CountAsync("messaging.outbox"));
        Assert.Equal(0, await f.CountAsync("raw_messages", "source_message_id = 'g04-fail'"));

        // History load → lane history, its own run; live → lane live.
        var history = await f.IngestAsync("g04-h", "Історичний пост.", enqueue: false);
        Assert.True(history.IsNew);
        Assert.Equal(1, await f.CountAsync("messaging.outbox", "lane = 'history' AND routing_key = 'puluj.history.raw.stored'"));
        Assert.Equal(2, await f.CountAsync("processing.runs", "state = 'running'"));
        f.Evidence.Record("G04-bridge-atomicity", new { duplicate_outbox_rows = 0, failed_outbox_rolls_back_raw = true, history_lane = true });
    }

    [Fact]
    public async Task G05_Incompatible_schema_and_unknown_event_are_quarantined_without_retries()
    {
        await f.ResetAsync();
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "fixtures", "valid", "raw.stored.json"));
        var major = JsonNode.Parse(fixture)!.AsObject();
        major["event_id"] = Guid.CreateVersion7().ToString();
        major["schema_version"] = "2.0";
        var unknown = JsonNode.Parse(fixture)!.AsObject();
        unknown["event_id"] = Guid.CreateVersion7().ToString();
        unknown["event_type"] = "raw.exploded";
        await f.PublishRawAsync("puluj.live.raw.stored", System.Text.Encoding.UTF8.GetBytes(major.ToJsonString()), major["event_id"]!.GetValue<string>());
        await f.PublishRawAsync("puluj.live.raw.stored", System.Text.Encoding.UTF8.GetBytes(unknown.ToJsonString()), unknown["event_id"]!.GetValue<string>());
        await f.PublishRawAsync("puluj.live.raw.stored", "this is not json"u8.ToArray(), "garbage");
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.quarantine") == 3));
        }
        finally
        {
            await f.Archive.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("processing.quarantine", "reason = 'incompatible_schema'"));
        Assert.Equal(1, await f.CountAsync("processing.quarantine", "reason = 'unknown_event'"));
        Assert.Equal(1, await f.CountAsync("processing.quarantine", "reason = 'invalid_payload'"));
        Assert.Equal(0, await f.CountAsync("processing.attempts"));
        Assert.Equal(0, await f.CountAsync("messaging.events"));
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync("puluj.archive.live.dlq")).Messages == 3));
        f.Evidence.Record("G05-schema-quarantine", new { quarantined = 3, attempts = 0, dlq = 3 });
    }
}
