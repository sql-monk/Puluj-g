using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P04 (plan §6.1, ADR-0003, ADR-0004 W1a/W1b): collectors' outbox + checkpoint in one transaction, raw-writer identity,
/// `raw.stored{is_new}`, history drain, the rebuild lock. Every assert is on committed rows.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class IngressTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string RawWriterQueue = "puluj.raw-writer.live";

    private async Task StartAllAsync()
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
    }

    private async Task StopAllAsync()
    {
        await f.Archive.StopAsync(None);
        await f.RawWriter.StopAsync(None);
        await f.Relay.StopAsync(None);
    }

    [Fact]
    public async Task C01_W1a_Reread_after_collector_crash_stores_once_and_reports_is_new_false()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var msg = f.Message("p04-c01", "Шахеди на Сумщині курсом на Полтавщину.");
        // The collector published (outbox + checkpoint committed), crashed before it could remember anything else,
        // re-read the source from the checkpoint and published the same post again.
        var first = await f.Ingress.PublishAsync(msg, source, "telegram", new CollectorCheckpoint("p04-c01", msg.PublishedAt), live: true, None);
        var second = await f.Ingress.PublishAsync(msg, source, "telegram", new CollectorCheckpoint("p04-c01", msg.PublishedAt), live: true, None);
        Assert.True(first.Accepted && second.Accepted);
        Assert.Null(first.IsNew); // unknown until the raw-writer runs
        Assert.Equal(2, await f.CountAsync("messaging.outbox", "event_type = 'ingress.received' AND confirmed_at IS NULL"));
        Assert.Equal(1, await f.ScalarAsync<long>("SELECT count(DISTINCT envelope->>'correlation_id') FROM messaging.outbox"));
        Assert.Equal(2, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NULL"));
        Assert.Equal(2, await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL"));
        Assert.Equal("p04-c01", await f.ScalarAsync<string>("SELECT last_source_message_id FROM collector_states WHERE source_id = @s", ("s", f.SourceId)));
        Assert.Equal(0, await f.CountAsync("raw_messages"));

        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.events") == 4, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await StopAllAsync();
        }
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        var raw = await f.ScalarAsync<string>("SELECT source_message_id || '|' || source_message_key || '|' || source_revision || '|' || hash || '|' || processing_status FROM raw_messages");
        var parts = raw.Split('|');
        Assert.Equal(["p04-c01", "p04-c01", "0"], parts[..3]);
        Assert.Equal(64, parts[3].Length);
        Assert.Equal("0", parts[4]); // Pending for the legacy processors
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'completed'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'noop'"));
        var stored = await StoredEventsAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal([false, true], stored.Select(e => e["payload"]!["is_new"]!.GetValue<bool>()).Order());
        var ingressIds = await IngressEventIdsAsync();
        Assert.All(stored, e => Assert.Contains(e["causation_id"]!.GetValue<string>(), ingressIds));
        Assert.Single(stored.Select(e => e["correlation_id"]!.GetValue<string>()).Distinct());
        Assert.All(stored, e => Assert.Equal(parts[3], e["payload"]!["content_hash"]!.GetValue<string>()));
        f.Evidence.Record("P04-C01", new { window = "W1a", ingress_events = 2, raw = 1, raw_stored = new { is_new_true = 1, is_new_false = 1 }, archive_events = 4, receipts = new { completed = 1, noop = 1 } });
    }

    [Fact]
    public async Task C02_Outbox_and_checkpoint_commit_together_and_the_checkpoint_keeps_other_fields()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        await f.ExecAsync("""
            CREATE OR REPLACE FUNCTION public.p04_test_fail() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated checkpoint failure'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER p04_test_fail BEFORE INSERT OR UPDATE ON collector_states FOR EACH ROW EXECUTE FUNCTION public.p04_test_fail();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => f.Ingress.PublishAsync(f.Message("p04-c02", "Пост, чий checkpoint не записався."), source, "telegram", new CollectorCheckpoint("p04-c02", null), live: true, None));
        }
        finally
        {
            await f.ExecAsync("DROP TRIGGER p04_test_fail ON collector_states; DROP FUNCTION public.p04_test_fail();");
        }
        Assert.Equal(0, await f.CountAsync("messaging.outbox"));
        Assert.Equal(0, await f.CountAsync("collector_states", "source_id = " + f.SourceId));

        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Ingress.PublishAsync(f.Message("p04-c02", "Перший пост."), source, "telegram", new CollectorCheckpoint("p04-c02", at), live: true, None);
        Assert.Equal(1, await f.CountAsync("messaging.outbox"));
        Assert.Equal("p04-c02", await f.ScalarAsync<string>("SELECT last_source_message_id FROM collector_states WHERE source_id = @s", ("s", f.SourceId)));
        // A history page writes only the cursor and the date: the id checkpoint of the live handler survives.
        var cursor = JsonDocument.Parse("{\"history\":{\"lastId\":42}}");
        await f.Ingress.PublishAsync(f.Message("p04-c02-h", "Історична сторінка.", at.AddDays(-30)), source, "telegram", new CollectorCheckpoint(null, at.AddDays(-30), cursor), live: false, None);
        var state = await f.States.GetAsync(f.SourceId, None);
        Assert.Equal("p04-c02", state.LastSourceMessageId);
        Assert.Equal(at.ToUnixTimeMilliseconds(), state.LastMessageAt!.Value.ToUnixTimeMilliseconds()); // the older date did not move it back
        Assert.Equal(42, state.Cursor!.RootElement.GetProperty("history").GetProperty("lastId").GetInt32());
        Assert.Null(state.LastError);
        f.Evidence.Record("P04-C02", new { window = "§6.1 outbox+checkpoint", trigger_fail = new { outbox = 0, checkpoint = 0 }, then = new { outbox = 2, last_id = "p04-c02", cursor_last_id = 42 } });
    }

    [Fact]
    public async Task C03_Identity_new_id_same_text_is_a_new_post_redelivery_is_not_edit_is_a_revision()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        const string text = "Той самий текст під різними id.";
        await f.Ingress.PublishAsync(f.Message("p04-c03-a", text), source, "telegram", null, live: true, None);
        await f.Ingress.PublishAsync(f.Message("p04-c03-b", text), source, "telegram", null, live: true, None);
        await f.Ingress.PublishAsync(f.Message("p04-c03-a", text), source, "telegram", null, live: true, None); // redelivery
        var edit = f.Message("p04-c03-a:e1789466500", text + " UPD", DateTimeOffset.UtcNow) with { SourceMessageKey = "p04-c03-a", SourceRevision = "e1789466500" };
        await f.Ingress.PublishAsync(edit, source, "telegram", null, live: true, None);
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NOT NULL") == 4, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await StopAllAsync();
        }
        Assert.Equal(3, await f.CountAsync("raw_messages"));
        Assert.Equal(2, await f.CountAsync("raw_messages", "hash = (SELECT hash FROM raw_messages WHERE source_message_id = 'p04-c03-a')")); // same content, two posts
        Assert.Equal(1, await f.CountAsync("raw_messages", "source_message_key = 'p04-c03-a' AND source_revision = '0'"));
        Assert.Equal(1, await f.CountAsync("raw_messages", "source_message_key = 'p04-c03-a' AND source_revision = 'e1789466500' AND source_message_id = 'p04-c03-a:e1789466500'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'noop'"));
        // The original and its revision share the correlation id; the other post does not.
        Assert.Equal(1, await f.ScalarAsync<long>("SELECT count(DISTINCT envelope->>'correlation_id') FROM messaging.outbox WHERE event_type = 'raw.stored' AND envelope->>'source_message_key' = 'p04-c03-a'"));
        Assert.Equal(2, await f.ScalarAsync<long>("SELECT count(DISTINCT envelope->>'correlation_id') FROM messaging.outbox WHERE event_type = 'raw.stored'"));
        f.Evidence.Record("P04-C03", new { rule = "§5.2 identity vs similarity", ingress = 4, raw = 3, same_hash_rows = 2, redelivery_noop = 1, revisions_of_a = 2 });
    }

    [Fact]
    public async Task C04_Alerts_start_end_keys_and_the_direct_store_agree_on_hash_and_identity()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var alert = JsonDocument.Parse("{\"kind\":\"alert.started\",\"at\":\"2026-09-15T09:58:00Z\",\"alert\":{\"id\":31,\"location_uid\":\"12\"}}");
        var start = f.Message("31:start", null) with { RawPayload = alert, SourceMessageKey = "31:start", SourceRevision = "0" };
        var end = f.Message("31:end", null) with { RawPayload = JsonDocument.Parse("{\"kind\":\"alert.finished\",\"at\":\"2026-09-15T10:41:00Z\",\"alert\":{\"id\":31}}"), SourceMessageKey = "31:end", SourceRevision = "0" };
        await f.Ingress.PublishAsync(start, source, "alerts.in.ua", null, live: true, None);
        await f.Ingress.PublishAsync(end, source, "alerts.in.ua", null, live: true, None);
        await f.Ingress.PublishAsync(end, source, "alerts.in.ua", null, live: true, None); // the next poll repeats the end
        // The same post stored directly (bridge/legacy path) must carry the same hash and identity as through the ingress.
        var directMsg = f.Message("p04-c04-direct", "Прямий шлях і ingress мають збігтися.") with { RawPayload = JsonDocument.Parse("{ \"b\": 1,  \"a\": [1, 2] }") };
        var direct = await f.Ingestor.IngestAsync(directMsg, f.SourceCode, None);
        Assert.True(direct.IsNew);
        var viaIngress = f.Message("p04-c04-ingress", directMsg.RawText!) with { RawPayload = directMsg.RawPayload };
        await f.Ingress.PublishAsync(viaIngress, source, "telegram", null, live: true, None);
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NOT NULL") == 4, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await StopAllAsync();
        }
        Assert.Equal(1, await f.CountAsync("raw_messages", "source_message_key = '31:start' AND source_revision = '0' AND source_message_id = '31:start'"));
        Assert.Equal(1, await f.CountAsync("raw_messages", "source_message_key = '31:end' AND source_revision = '0'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'noop'"));
        var hashes = await f.ScalarAsync<string>("SELECT string_agg(hash, ',' ORDER BY source_message_id) FROM raw_messages WHERE source_message_id IN ('p04-c04-direct', 'p04-c04-ingress')");
        var pair = hashes.Split(',');
        Assert.Equal(pair[0], pair[1]);
        // A direct repeat returns the existing id, no new row.
        var again = await f.Ingestor.IngestAsync(directMsg, f.SourceCode, None);
        Assert.False(again.IsNew);
        Assert.Equal(direct.RawMessageId, again.RawMessageId);
        Assert.Equal(4, await f.CountAsync("raw_messages"));
        f.Evidence.Record("P04-C04", new { alerts = new { start = 1, end = 1, end_repeat_noop = 1 }, cross_mode_hash_equal = true, direct_repeat_returns_existing_id = true });
    }

    [Fact]
    public async Task C05_History_lane_stays_pending_and_drain_waits_for_the_raw_writer()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        for (var i = 0; i < 3; i++)
        {
            await f.Ingress.PublishAsync(f.Message($"p04-c05-{i}", $"Історія {i}", DateTimeOffset.UtcNow.AddDays(-10)), source, "telegram", null, live: false, None);
        }
        Assert.Equal(3, await f.CountAsync("messaging.outbox", "lane = 'history' AND routing_key = 'puluj.history.ingress.received'"));
        Assert.Equal(3, await f.IngressWriter.PendingRawWritesAsync([f.SourceId], None));
        Assert.False(await f.Ingress.WaitForDrainAsync([f.SourceId], None)); // nothing consumes yet → timeout (3 s in tests)

        await StartAllAsync();
        try
        {
            Assert.True(await f.Ingress.WaitForDrainAsync([f.SourceId], None));
        }
        finally
        {
            await StopAllAsync();
        }
        Assert.Equal(3, await f.CountAsync("raw_messages", "processing_status = 0"));
        Assert.Equal(0, await f.IngressWriter.PendingRawWritesAsync([f.SourceId], None));
        Assert.Equal(3, await f.CountAsync("messaging.outbox", "event_type = 'raw.stored' AND lane = 'history'"));
        f.Evidence.Record("P04-C05", new { lane = "history", pending_before = 3, drain_before_consumer = false, drain_after = true, raw_pending = 3 });
    }

    [Fact(Skip = "W1c: a Telegram edit update lost between receipt and commit is only recoverable from WTelegram's local update state (not durable per §6.1); needs an MTProto session — documented limit until a durable spool exists")]
    public void C06_W1c_Telegram_edit_update_before_commit() { }

    [Fact]
    public async Task C07_Rebuild_lock_on_raw_messages_makes_the_raw_writer_wait_not_fail()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message("p04-c07", "Пост під час rebuild."), source, "telegram", null, live: true, None);
        await f.Relay.RelayOnceAsync(None);

        await using var db = await f.Factory.CreateDbContextAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var lockTx = await conn.BeginTransactionAsync();
        await using (var l = new NpgsqlCommand("LOCK TABLE raw_messages IN ACCESS EXCLUSIVE MODE", conn, lockTx))
        {
            await l.ExecuteNonQueryAsync();
        }
        await f.RawWriter.StartAsync(None);
        try
        {
            await Task.Delay(3000);
            Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'raw-writer' AND state = 'running'"));
            Assert.Equal(0, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NOT NULL"));
            await lockTx.RollbackAsync(); // the rebuild ends
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'completed'") == 1, TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await f.RawWriter.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'raw-writer'"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'raw-writer' AND state = 'succeeded'"));
        Assert.Equal(0, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P04-C07", new { window = "ReprocessService lock", waited_seconds = 3, attempts = 1, failed = 0, quarantine = 0 });
    }

    [Fact]
    public async Task G01_Failing_post_commit_hook_still_acks_and_two_replicas_keep_one_raw_row()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        // Phase 1: the only consumer is a raw-writer whose post-commit hook (the NOTIFY) throws — every delivery goes through it.
        var msg = f.Message("p04-g01", "Хук після commit падає.");
        for (var i = 0; i < 3; i++)
        {
            await f.Ingress.PublishAsync(msg, source, "telegram", null, live: true, None);
        }
        await f.Relay.RelayOnceAsync(None);
        Assert.Equal(3u, (await f.QueueAsync(RawWriterQueue)).Messages);
        var throwing = f.NewConsumer(new ThrowingAfterCommitHandler(f.Services.GetRequiredService<RawWriterHandler>()), "raw-writer@throwing");
        await throwing.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NOT NULL") == 3, TimeSpan.FromSeconds(30)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(RawWriterQueue)).Messages == 0)); // acknowledged despite the throwing hook
        }
        finally
        {
            await throwing.StopAsync(None);
        }
        Assert.Equal(3, throwing.Delivered);
        Assert.Equal(1, await f.CountAsync("raw_messages"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'completed'"));
        Assert.Equal(2, await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome = 'noop'"));

        // Phase 2: two replicas share three copies of another post (whoever wins the insert reports is_new).
        var other = f.Message("p04-g01-b", "Паралельні репліки.");
        for (var i = 0; i < 3; i++)
        {
            await f.Ingress.PublishAsync(other, source, "telegram", null, live: true, None);
        }
        await f.Relay.RelayOnceAsync(None);
        var replica = f.NewConsumer(worker: "raw-writer@replica-2", handler: f.Services.GetRequiredService<RawWriterHandler>());
        await f.RawWriter.StartAsync(None);
        await replica.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'raw-writer' AND outcome IS NOT NULL") == 6, TimeSpan.FromSeconds(30)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.QueueAsync(RawWriterQueue)).Messages == 0));
        }
        finally
        {
            await f.RawWriter.StopAsync(None);
            await replica.StopAsync(None);
        }
        Assert.Equal(2, await f.CountAsync("raw_messages"));
        Assert.Equal(3, f.RawWriter.Delivered + replica.Delivered);
        Assert.Equal(6, await f.CountAsync("messaging.outbox", "event_type = 'raw.stored'"));
        Assert.Equal(2, await f.CountAsync("messaging.outbox", "event_type = 'raw.stored' AND (envelope->'payload'->>'is_new')::boolean"));
        Assert.Equal(0, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P04-G01", new { after_commit_throw = new { deliveries = throwing.Delivered, acked = true, raw = 1, noop = 2 }, replicas = new { r1 = f.RawWriter.Delivered, r2 = replica.Delivered, raw_total = 2, is_new_true = 2 } });
    }

    private async Task<List<JsonNode>> StoredEventsAsync()
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var rows = await db.Outbox.AsNoTracking().Where(o => o.EventType == "raw.stored").ToListAsync();
        return rows.Select(o => JsonNode.Parse(o.Envelope.RootElement.GetRawText())!).ToList();
    }

    private async Task<HashSet<string>> IngressEventIdsAsync()
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var ids = await db.Outbox.Where(o => o.EventType == "ingress.received").Select(o => o.EventId).ToListAsync();
        return ids.Select(id => id.ToString()).ToHashSet();
    }
}
