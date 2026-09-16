using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging.Tests.Unit;
using Puluj.Processing.Stages;
using Puluj.Processing.Writers;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P09: the domain writers on real PostGIS + RabbitMQ — fact rows per observation, tracks and alert intervals with
/// revisions and change events, the lock hierarchy under a real candidate-create race, cross-region/time-window
/// correlation, alert start/end/cancel, watchdog expiry commands with the revision check, parity with the legacy
/// processor, the double-writer guards and a hot-row benchmark. Every assert is on committed rows or outbox events.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class DomainWriterTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly JsonSchema TrackChanged = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "track.changed.schema.json"));
    private static readonly JsonSchema AlertChanged = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "alert.changed.schema.json"));
    private static readonly JsonSchema TrackExpiry = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "track.expiry.requested.schema.json"));
    private const string Shahed = "Шахеди на Сумщині курсом на Полтавщину.";

    private async Task StartAllAsync(bool writers = true)
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Parser.StartAsync(None);
        await f.Finalizer.StartAsync(None);
        if (writers)
        {
            await f.TrackWorker.StartAsync(None);
            await f.AlertWorker.StartAsync(None);
            await f.IncidentWorker.StartAsync(None);
        }
    }

    private async Task StopAllAsync()
    {
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

    /// <summary>Publishes through the collectors' ingress and runs the whole platform path until every track/alert delivery has a receipt.</summary>
    private async Task RunAsync(bool writers, params (string Id, string? Text, DateTimeOffset At, JsonDocument? Payload)[] messages)
    {
        var source = await f.SourceAsync();
        foreach (var (id, text, at, payload) in messages)
        {
            await f.Ingress.PublishAsync(f.Message(id, text, at) with { RawPayload = payload }, source, "test", null, live: true, None);
        }
        await StartAllAsync(writers);
        try
        {
            await SettleAsync(writers, messages.Length);
        }
        finally
        {
            await StopAllAsync();
        }
    }

    /// <summary>One message at a time (ordered input, plan §7): the parity test compares against the legacy loop, which processes in id order.</summary>
    private async Task RunOrderedAsync(bool writers, params (string Id, string? Text, DateTimeOffset At, JsonDocument? Payload)[] messages)
    {
        var source = await f.SourceAsync();
        await StartAllAsync(writers);
        try
        {
            var n = (int)await f.CountAsync("processing.extractions"); // a second ordered batch in the same test continues the count
            foreach (var (id, text, at, payload) in messages)
            {
                await f.Ingress.PublishAsync(f.Message(id, text, at) with { RawPayload = payload }, source, "test", null, live: true, None);
                await SettleAsync(writers, ++n);
            }
        }
        finally
        {
            await StopAllAsync();
        }
    }

    private async Task SettleAsync(bool writers, int messages)
    {
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions") == messages, TimeSpan.FromSeconds(60)),
            $"extractions: {await f.CountAsync("processing.extractions")} of {messages}");
        if (writers)
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id IN ('track-worker', 'alert-worker', 'incident-worker') AND outcome IS NULL") == 0, TimeSpan.FromSeconds(60)),
                "writer deliveries settled");
        }
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(40)));
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(40)));
    }

    private async Task<List<JsonNode>> EventsAsync(string eventType)
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var rows = await db.Outbox.AsNoTracking().Where(o => o.EventType == eventType).OrderBy(o => o.OutboxId).ToListAsync();
        return rows.Select(o => JsonNode.Parse(o.Envelope.RootElement.GetRawText())!).ToList();
    }

    private static void Valid(JsonSchema schema, JsonNode payload)
    {
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(payload), ContractSchemas.Options);
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct()));
    }

    private static void ValidEnvelope(JsonNode envelope)
    {
        var result = ContractSchemas.Envelope.Evaluate(JsonSerializer.SerializeToElement(envelope), ContractSchemas.Options);
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct()));
    }

    private static DateTimeOffset Recent(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

    [Fact]
    public async Task W01_Observation_becomes_a_target_row_a_track_and_a_valid_track_changed_once()
    {
        await f.ResetAsync();
        await RunAsync(true, ("w01", Shahed, Recent(5), null));
        Assert.Equal(1, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("target_tracks", "status = 0 AND revision = 1 AND last_event_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.observations", "legacy_target_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome = 'completed'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'alert-worker' AND outcome = 'noop'")); // expected by the manifest branch, nothing to do
        var changed = Assert.Single(await EventsAsync("track.changed"));
        ValidEnvelope(changed);
        Valid(TrackChanged, changed["payload"]!);
        Assert.Equal("created", changed["payload"]!["change"]!.GetValue<string>());
        Assert.Equal(1, changed["payload"]!["revision"]!.GetValue<int>());
        Assert.Equal(1, changed["aggregate_revision"]!.GetValue<int>());
        Assert.Equal("track:1", changed["aggregate_id"]!.GetValue<string>());
        Assert.Single(changed["payload"]!["observation_ids"]!.AsArray());
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome = 'completed' AND event_id = @e".Replace("@e", $"'{changed["event_id"]!.GetValue<string>()}'"))); // routable (review B1)
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome = 'completed' AND event_id = (SELECT event_id FROM messaging.outbox WHERE event_type = 'observations.recorded')")); // expected set by manifest (review B2)

        // The same event again (at-least-once): the inbox fast path, no second row.
        var observed = await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'observations.recorded'");
        await f.TrackWorker.StartAsync(None);
        try
        {
            await f.PublishRawAsync("puluj.live.observations.recorded", Encoding.UTF8.GetBytes(observed), JsonNode.Parse(observed)!["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(f.TrackWorker.Duplicates >= 1), TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await f.TrackWorker.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("targets"));
        Assert.Equal(1, await f.CountAsync("target_tracks"));
        f.Evidence.Record("P09-W01", new { targets = 1, tracks = 1, revision = 1, track_changed = "created", alert_worker = "noop", duplicate = "inbox fast path" });
    }

    /// <summary>Both replicas past PrepareAsync, then both into the transaction: the locks serialize the candidate read.</summary>
    private sealed class BarrierHooks(int parties) : ConsumerHooks
    {
        private readonly Barrier _barrier = new(parties);
        public int Met;
        public override void BeforeCommit(Envelope envelope)
        {
            if (envelope.EventType == "observations.recorded" && _barrier.SignalAndWait(TimeSpan.FromSeconds(20)))
            {
                Interlocked.Increment(ref Met); // both replicas were here together: the transactions that follow start concurrently
            }
        }
    }

    [Fact]
    public async Task W02_Candidate_create_race_two_replicas_one_track()
    {
        await f.ResetAsync();
        var replica = f.NewConsumer(f.Services.GetRequiredService<TrackWriterHandler>(), "track-worker@replica-2");
        var hooks = new BarrierHooks(2);
        f.TrackWorker.Hooks = hooks;
        replica.Hooks = hooks;
        var source = await f.SourceAsync();
        // Two sightings of the same object 5 minutes apart (outside the 3-minute duplicate window, inside the candidate window): the
        // second must find the first one's track under the lock — without it both replicas would decide "no candidate" and open two.
        var at = Recent(10);
        await f.Ingress.PublishAsync(f.Message("w02-a", Shahed, at), source, "test", null, live: true, None);
        var source2 = await f.SourceAsync(MessagingFixture.SourceCode2);
        await f.Ingress.PublishAsync(f.Message("w02-b", "Шахеди на Сумщині, курс на Полтавщину.", at.AddMinutes(5), sourceId: source2.SourceId), source2, "test", null, live: true, None);
        await StartAllAsync();
        await replica.StartAsync(None);
        try
        {
            await SettleAsync(true, 2);
            Assert.True(f.TrackWorker.Delivered >= 1 && replica.Delivered >= 1, $"both replicas took a delivery: {f.TrackWorker.Delivered}/{replica.Delivered}");
            Assert.Equal(2, hooks.Met); // the barrier actually held both (a timeout would let them run one after the other)
        }
        finally
        {
            f.TrackWorker.Hooks = ConsumerHooks.None;
            await replica.StopAsync(None);
            await StopAllAsync();
        }
        Assert.Equal(2, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("target_tracks"));
        Assert.Equal(2, await f.CountAsync("track_targets"));
        Assert.Equal(0, await f.CountAsync("targets", "duplicate_of_target_id IS NOT NULL")); // attached as a candidate, not as a duplicate
        var changes = await EventsAsync("track.changed");
        Assert.Equal(["created", "updated"], changes.Select(c => c["payload"]!["change"]!.GetValue<string>()));
        Assert.Equal([1, 2], changes.Select(c => c["payload"]!["revision"]!.GetValue<int>()));
        f.Evidence.Record("P09-W02", new { replicas = 2, barrier = "BeforeCommit (both met)", path = "candidate (5 min apart)", tracks = 1, targets = 2, changes = "created, updated", revisions = "1, 2" });
    }

    [Fact]
    public async Task W03_Cross_region_and_time_window_give_separate_tracks_duplicates_link()
    {
        await f.ResetAsync();
        var t0 = Recent(300);
        await RunAsync(true,
            ("w03-a", Shahed, t0, null),
            ("w03-b", "Шахеди на Харківщині.", t0.AddMinutes(1), null), // another oblast: no candidate
            ("w03-c", "Шахеди на Сумщині курсом на Полтавщину.", t0.AddMinutes(240), null)); // same place, outside the 120-minute window: a new track
        Assert.Equal(3, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        Assert.Equal(3, await f.CountAsync("target_tracks"));
        Assert.Equal(3, await f.CountAsync("track_targets"));
        var changes = await EventsAsync("track.changed");
        Assert.Equal(3, changes.Count(c => c["payload"]!["change"]!.GetValue<string>() == "created"));
        Assert.All(changes, c => Assert.Equal("UAV", c["payload"]!["category"]!.GetValue<string>())); // taxonomy code, not the id

        // target.cancelled without a category ("загроза минула") takes the whole track scope and cancels the active tracks of the region.
        await RunOrderedAsync(true, ("w03-d", "Загроза для Сумщини минула.", t0.AddMinutes(245), null));
        Assert.Equal(2, await f.CountAsync("target_tracks", "status = 2 AND closed_reason = 'target_cancelled'")); // both Sumy tracks were in the air at that moment
        Assert.Equal(1, await f.CountAsync("target_tracks", "status = 0")); // Kharkiv: another oblast, untouched
        var cancelled = (await EventsAsync("track.changed")).Where(c => c["payload"]!["change"]!.GetValue<string>() == "cancelled").ToList();
        Assert.Equal(2, cancelled.Count);
        Assert.All(cancelled, c => Assert.Equal("target_cancelled", c["payload"]!["reason"]!.GetValue<string>()));
        f.Evidence.Record("P09-W03", new { messages = 4, tracks = 3, cross_region = "separate", outside_window = "separate", target_cancelled = "both Sumy tracks cancelled, Kharkiv untouched (exclusive track scope)" });
    }

    [Fact]
    public async Task W04_Alert_start_end_cancel_text_and_structured()
    {
        await f.ResetAsync();
        var t0 = Recent(90);
        var start = JsonDocument.Parse($"{{\"kind\":\"alert.started\",\"at\":\"{FactMapper.Iso(t0)}\",\"alert\":{{\"id\":31,\"location_title\":\"Київська область\",\"location_oblast\":\"Київська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"started_at\":\"{FactMapper.Iso(t0)}\"}}}}");
        var end = JsonDocument.Parse($"{{\"kind\":\"alert.finished\",\"at\":\"{FactMapper.Iso(t0.AddMinutes(43))}\",\"alert\":{{\"id\":31,\"location_title\":\"Київська область\",\"location_oblast\":\"Київська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"started_at\":\"{FactMapper.Iso(t0)}\",\"finished_at\":\"{FactMapper.Iso(t0.AddMinutes(43))}\"}}}}");
        await RunOrderedAsync(true, // ordered: the cancellation must see the track and the structured end must precede its start
            ("w04-track", "Шахеди на Харківщині.", t0.AddMinutes(5), null),
            ("w04-alert", "Повітряна тривога в Харківській області.", t0.AddMinutes(6), null),
            ("31:end", null, t0.AddMinutes(43), end), // structured end before its start (history order)
            ("31:start", null, t0, start),
            ("w04-cancel", "Відбій повітряної тривоги в Харківській області.", t0.AddMinutes(50), null),
            ("32:start", null, t0.AddMinutes(51), JsonDocument.Parse($"{{\"kind\":\"alert.started\",\"at\":\"{FactMapper.Iso(t0.AddMinutes(51))}\",\"alert\":{{\"id\":32,\"location_title\":\"Сумська область\",\"location_oblast\":\"Сумська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"alert_level\":\"yellow\",\"started_at\":\"{FactMapper.Iso(t0.AddMinutes(51))}\"}}}}")),
            ("32:start-again", null, t0.AddMinutes(52), JsonDocument.Parse($"{{\"kind\":\"alert.started\",\"at\":\"{FactMapper.Iso(t0.AddMinutes(52))}\",\"alert\":{{\"id\":32,\"location_title\":\"Сумська область\",\"location_oblast\":\"Сумська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"alert_level\":\"red\",\"started_at\":\"{FactMapper.Iso(t0.AddMinutes(51))}\"}}}}")));

        // Text interval: started, then ended by the cancellation; the track in the oblast is cancelled by it.
        Assert.Equal(1, await f.CountAsync("air_alerts", "source_alert_id LIKE 'text:%' AND ended_at IS NOT NULL AND revision = 2"));
        Assert.Equal(1, await f.CountAsync("target_tracks", "status = 2 AND closed_reason = 'alert_cancelled' AND revision = 2"));
        // Structured interval: one row whichever message came first, revision bumped by both deliveries.
        Assert.Equal(1, await f.CountAsync("air_alerts", "source_alert_id = '31' AND ended_at IS NOT NULL AND start_raw_message_id IS NOT NULL AND end_raw_message_id IS NOT NULL"));
        var alerts = await EventsAsync("alert.changed");
        foreach (var a in alerts)
        {
            ValidEnvelope(a);
            Valid(AlertChanged, a["payload"]!);
        }
        var text = alerts.Where(a => a["payload"]!["scope"]!["external_alert_id"] is null).Select(a => a["payload"]!["change"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "ended"], text);
        var structured = alerts.Where(a => a["payload"]!["scope"]!["external_alert_id"]?.GetValue<string>() == "31").Select(a => a["payload"]!["change"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "updated"], structured); // the end arrived first (a closed interval), the start filled in its message
        // A re-announced start with a new level is a change of the interval (review B1): updated, revision 2; the level travels in scope.
        var reannounced = alerts.Where(a => a["payload"]!["scope"]!["external_alert_id"]?.GetValue<string>() == "32").ToList();
        Assert.True(reannounced.Count == 2, "alert.changed: " + string.Join(" | ", alerts.Select(a => a["payload"]!.ToJsonString())) + " || air_alerts: " + await f.ScalarAsync<string>("SELECT coalesce(json_agg(a)::text, '[]') FROM air_alerts a") + " || receipts: " + await f.ScalarAsync<string>("SELECT coalesce(json_agg(json_build_object('s', subscription_id, 'o', outcome, 'r', reason))::text, '[]') FROM processing.deliveries WHERE subscription_id = 'alert-worker'"));
        Assert.Equal(["started", "updated"], reannounced.Select(a => a["payload"]!["change"]!.GetValue<string>()));
        Assert.Equal(["yellow", "red"], reannounced.Select(a => a["payload"]!["scope"]!["level"]!.GetValue<string>()));
        Assert.Equal(1, await f.CountAsync("air_alerts", "source_alert_id = '32' AND level = 2 AND revision = 2"));
        var cancelled = Assert.Single(await EventsAsync("track.changed"), c => c["payload"]!["change"]!.GetValue<string>() == "cancelled");
        Assert.Equal("alert_cancelled", cancelled["payload"]!["reason"]!.GetValue<string>());
        Assert.Equal(7, await f.CountAsync("targets", "observation_id IS NOT NULL")); // one fact row per message
        f.Evidence.Record("P09-W04", new { text_alert = "started → ended, track cancelled", structured = "end before start → one interval (started, updated)", level_reannounce = "updated, revision 2, scope.level red", alert_changed = alerts.Count });
    }

    [Fact]
    public async Task W05_Expiry_command_respects_revision_watermark_and_backlog()
    {
        await f.ResetAsync();
        var t0 = Recent(600); // 10 hours ago: past 2 × 30 min for any class
        await RunAsync(true, ("w05", Shahed, t0, null));
        Assert.Equal(1, await f.CountAsync("target_tracks", "status = 0 AND revision = 1"));

        // Stale command (revision 0 ≠ 1) → noop; duplicate publish → inbox fast path.
        var observed = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'observations.recorded'"))!.AsObject();
        var stale = Command(observed, 1, expectedRevision: 0, t0.AddHours(1), DateTimeOffset.UtcNow);
        await f.TrackWorker.StartAsync(None);
        try
        {
            await f.PublishRawAsync("puluj.live.track.expiry.requested", Encoding.UTF8.GetBytes(stale.ToJsonString()), stale["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome = 'noop' AND reason LIKE 'stale_revision%'") == 1, TimeSpan.FromSeconds(20)));
            await f.PublishRawAsync("puluj.live.track.expiry.requested", Encoding.UTF8.GetBytes(stale.ToJsonString()), stale["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(f.TrackWorker.Duplicates >= 1), TimeSpan.FromSeconds(20)));
            Assert.Equal(1, await f.CountAsync("target_tracks", "status = 0 AND revision = 1"));

            // Backlog (review B5): an old message in flight holds the watermark back — no command for a track younger than it.
            var source = await f.SourceAsync();
            await f.Ingress.PublishAsync(f.Message("w05-old", "Вибухи у Дніпрі.", t0.AddHours(-48)), source, "test", null, live: true, None); // relay stopped: unconfirmed outbox row
            var held = await f.Watchdog.SweepAsync(None);
            Assert.True(held.Watermark <= t0.AddHours(-47), $"watermark {held.Watermark:O} follows the backlog");
            Assert.Equal(0, held.TrackCommands);
            await f.ExecAsync("DELETE FROM messaging.outbox WHERE event_type = 'ingress.received' AND (envelope->>'source_message_key') = 'w05-old'");

            // The real sweep: a command with the current revision, delivered through the relay → expired.
            var sweep = await f.Watchdog.SweepAsync(None);
            Assert.Equal(1, sweep.TrackCommands);
            var again = await f.Watchdog.SweepAsync(None);
            Assert.Equal(0, again.TrackCommands); // memo: not commanded twice
            var command = Assert.Single(await EventsAsync("track.expiry.requested"));
            ValidEnvelope(command);
            Valid(TrackExpiry, command["payload"]!);
            Assert.Equal(1, command["payload"]!["expected_revision"]!.GetValue<int>());
            await f.Relay.StartAsync(None);
            await f.Archive.StartAsync(None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("target_tracks", "status = 1 AND closed_reason = 'timeout' AND revision = 2") == 1, TimeSpan.FromSeconds(30)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await f.Relay.StopAsync(None);
            await f.TrackWorker.StopAsync(None);
        }
        var expired = Assert.Single(await EventsAsync("track.changed"), c => c["payload"]!["change"]!.GetValue<string>() == "expired");
        Valid(TrackChanged, expired["payload"]!);
        Assert.Equal(2, expired["payload"]!["revision"]!.GetValue<int>());
        f.Evidence.Record("P09-W05", new { stale_revision = "noop", duplicate_command = "inbox fast path", backlog = "watermark held, 0 commands", sweep = "1 command, memo blocks the repeat", expired = "revision 2" });
    }

    private static JsonObject Command(JsonObject cause, long trackId, int expectedRevision, DateTimeOffset expireAt, DateTimeOffset watermark)
    {
        var c = cause.DeepClone().AsObject();
        c["event_id"] = Guid.CreateVersion7().ToString();
        c["event_type"] = "track.expiry.requested";
        c["producer"] = "watchdog@test";
        c["causation_id"] = cause["event_id"]!.GetValue<string>();
        c["aggregate_id"] = $"track:{trackId}";
        c["aggregate_revision"] = expectedRevision;
        c["payload"] = new JsonObject
        {
            ["track_id"] = trackId,
            ["expected_revision"] = expectedRevision,
            ["expire_at"] = FactMapper.Iso(expireAt),
            ["watermark"] = FactMapper.Iso(watermark),
            ["reason"] = "timeout",
        };
        return c;
    }

    private static readonly (string Id, string Text, int MinutesAgo)[] ParityCorpus =
    [
        ("p-1", "Шахеди на Сумщині курсом на Полтавщину.", 100),
        ("p-2", "БпЛА на Полтавщині у південному напрямку.", 70),
        ("p-3", "Вибухи у Харкові.", 60),
        ("p-4", "Повітряна тривога в Харківській області.", 59),
        ("p-5", "Шахеди на Харківщині.", 55),
        ("p-6", "Відбій повітряної тривоги в Харківській області.", 20),
        // No near-duplicates across messages: which of two sightings inside the duplicate window becomes the original depends on
        // processing order, and the platform's delivery order is not the legacy loop's id order (plan §7: reproducible rebuilds need ordered input).
        ("p-7", "Шахеди на Сумщині курсом на Полтавщину.", 88),
    ];

    [Fact]
    public async Task W06_Writers_match_the_legacy_processor_on_targets_tracks_and_alerts()
    {
        // Path A: the platform pipeline without the writers, then the legacy processor on the same raw rows (as S08 does).
        await f.ResetAsync();
        var now = DateTimeOffset.UtcNow; // one clock base for both paths: the snapshots carry event times
        await RunOrderedAsync(false, ParityCorpus.Select(m => (m.Id, (string?)m.Text, now.AddMinutes(-m.MinutesAgo), (JsonDocument?)null)).ToArray());
        List<long> rawIds;
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            rawIds = await db.RawMessages.OrderBy(r => r.RawMessageId).Select(r => r.RawMessageId).ToListAsync();
        }
        foreach (var rawId in rawIds)
        {
            await f.LegacyProcessor.ProcessAsync(rawId, None);
        }
        var legacy = await SnapshotAsync();

        // Path B: the same messages through the writers.
        await f.ResetAsync();
        await RunOrderedAsync(true, ParityCorpus.Select(m => (m.Id, (string?)m.Text, now.AddMinutes(-m.MinutesAgo), (JsonDocument?)null)).ToArray());
        var writers = await SnapshotAsync();
        for (var i = 0; i < Math.Max(legacy.Targets.Count, writers.Targets.Count); i++)
        {
            Assert.True(i < legacy.Targets.Count && i < writers.Targets.Count && legacy.Targets[i] == writers.Targets[i],
                $"target #{i}: legacy {(i < legacy.Targets.Count ? legacy.Targets[i] : "-")} | writers {(i < writers.Targets.Count ? writers.Targets[i] : "-")}");
        }
        Assert.True(string.Join(Sep, legacy.Tracks) == string.Join(Sep, writers.Tracks), "tracks:" + Sep + "legacy:" + Sep + string.Join(Sep, legacy.Tracks) + Sep + "writers:" + Sep + string.Join(Sep, writers.Tracks));
        Assert.True(string.Join(Sep, legacy.Alerts) == string.Join(Sep, writers.Alerts), "alerts:" + Sep + "legacy:" + Sep + string.Join(Sep, legacy.Alerts) + Sep + "writers:" + Sep + string.Join(Sep, writers.Alerts));
        Assert.Equal(7, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        f.Evidence.Record("P09-W06", new { messages = ParityCorpus.Length, targets = legacy.Targets.Count, tracks = legacy.Tracks.Count, alerts = legacy.Alerts.Count, differences = 0 });
    }

    private static readonly string Sep = Environment.NewLine;

    private sealed record Snapshot(List<string> Targets, List<string> Tracks, List<string> Alerts);

    /// <summary>Canonical rows without ids and clock timestamps: what both writers must agree on.</summary>
    private async Task<Snapshot> SnapshotAsync()
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var targets = await db.Targets.AsNoTracking().Include(t => t.RawMessage).OrderBy(t => t.RawMessage!.SourceMessageId).ThenBy(t => t.SegmentIndex).ToListAsync();
        var byId = targets.ToDictionary(t => t.TargetId, t => t.RawMessage!.SourceMessageId + "#" + t.SegmentIndex);
        var targetRows = targets.Select(t => FactMapper.Canonical(new JsonObject
        {
            ["msg"] = t.RawMessage!.SourceMessageId,
            ["segment"] = t.SegmentIndex,
            ["event_type"] = t.EventType.ToString(),
            ["kind"] = t.EventKindId,
            ["level"] = t.AlertLevel.ToString(),
            ["category"] = t.TargetCategoryId,
            ["class"] = t.TargetClassId,
            ["family"] = t.TargetFamilyId,
            ["model"] = t.TargetModelId,
            ["confidence"] = t.Confidence.ToString(),
            ["model_confidence"] = t.ModelConfidence.ToString(),
            ["classification_confidence"] = t.ClassificationConfidence.ToString(),
            ["count"] = t.ObjectCount,
            ["approx"] = t.ObjectCountIsApproximate,
            ["location_kind"] = t.LocationKind.ToString(),
            ["place"] = t.LocationPlaceId,
            ["accuracy"] = t.LocationAccuracyKm is double a ? Math.Round(a, 3) : null,
            ["origin"] = t.OriginPlaceId,
            ["destination"] = t.DestinationPlaceId,
            ["direction"] = t.DirectionDeg is double d ? Math.Round(d, 2) : null,
            ["direction_kind"] = t.DirectionKind.ToString(),
            ["direction_confidence"] = t.DirectionConfidence.ToString(),
            ["method"] = t.IdentificationMethod.ToString(),
            ["parser"] = t.ParserVersion,
            ["text"] = t.SegmentText,
            ["duplicate_of"] = t.DuplicateOfTargetId is long dup ? byId[dup] : null,
            ["metadata"] = t.ParserMetadata is { } m ? JsonNode.Parse(m.RootElement.GetRawText()) : null,
        })).ToList();
        var tracks = await db.TargetTracks.AsNoTracking().Include(t => t.Targets).OrderBy(t => t.FirstSeenAt).ThenBy(t => t.LastLocationPlaceId).ToListAsync();
        var trackRows = tracks.Select(t => FactMapper.Canonical(new JsonObject
        {
            ["status"] = t.Status.ToString(),
            ["closed_reason"] = t.ClosedReason,
            ["category"] = t.TargetCategoryId,
            ["class"] = t.TargetClassId,
            ["first_seen"] = FactMapper.Iso(t.FirstSeenAt),
            ["last_seen"] = FactMapper.Iso(t.LastSeenAt),
            ["place"] = t.LastLocationPlaceId,
            ["targets"] = t.TargetCount,
            ["sources"] = t.DistinctSourceCount,
            ["confidence"] = t.TrackConfidence.ToString(),
            ["links"] = t.Targets.Count,
        })).ToList();
        var alerts = await db.AirAlerts.AsNoTracking().OrderBy(a => a.StartedAt).ThenBy(a => a.AirAlertId).ToListAsync();
        var alertRows = alerts.Select(a => FactMapper.Canonical(new JsonObject
        {
            ["place"] = a.PlaceId,
            ["key"] = a.SourceAlertId,
            ["level"] = a.Level.ToString(),
            ["started"] = FactMapper.Iso(a.StartedAt),
            ["ended"] = a.EndedAt is { } e ? FactMapper.Iso(e) : null,
            ["has_start_raw"] = a.StartRawMessageId is not null,
            ["has_end_raw"] = a.EndRawMessageId is not null,
        })).ToList();
        return new Snapshot(targetRows, trackRows, alertRows);
    }

    [Fact]
    public async Task W07_Double_writer_guards_in_both_directions()
    {
        // Legacy first: the writers see rows without observation_id → noop legacy_owned (held at the barrier while legacy writes).
        await f.ResetAsync();
        var gate = new SemaphoreSlim(0);
        var hooks = new GateHooks(gate);
        f.TrackWorker.Hooks = hooks;
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message("w07-a", Shahed, Recent(5)), source, "test", null, live: true, None);
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(hooks.Arrived), TimeSpan.FromSeconds(40)), "the writer reached the transaction");
            var rawId = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages");
            Assert.Equal(1, await f.LegacyProcessor.ProcessAsync(rawId, None)); // legacy writes while the writer waits
            gate.Release();
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome = 'noop' AND reason LIKE 'legacy_owned%'") == 1, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            gate.Release();
            f.TrackWorker.Hooks = ConsumerHooks.None;
            await StopAllAsync();
        }
        Assert.Equal(1, await f.CountAsync("targets", "observation_id IS NULL"));
        Assert.Equal(0, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("target_tracks"));

        // Writers first: the legacy processor finds observation-backed rows → marks the raw done without writing.
        await f.ResetAsync();
        await RunAsync(true, ("w07-b", Shahed, Recent(5), null));
        var raw = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages");
        Assert.Equal(0, await f.LegacyProcessor.ProcessAsync(raw, None));
        Assert.Equal(1, await f.CountAsync("targets"));
        Assert.Equal(1, await f.CountAsync("target_tracks"));
        Assert.Equal(1, await f.CountAsync("raw_messages", "processing_status = 1")); // Processed by the guard, not rewritten
        f.Evidence.Record("P09-W07", new { legacy_first = "writer noop legacy_owned (concurrent, gate)", writers_first = "legacy ProcessAsync → 0, status Processed, rows untouched" });
    }

    private sealed class GateHooks(SemaphoreSlim gate) : ConsumerHooks
    {
        public volatile bool Arrived;
        public override void BeforeCommit(Envelope envelope)
        {
            if (envelope.EventType == "observations.recorded")
            {
                Arrived = true;
                gate.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public async Task W08_Hot_row_benchmark_one_category_vs_two_through_two_replicas()
    {
        var oneCategory = await BenchmarkAsync("uav", Enumerable.Range(0, 60).Select(i => $"Шахеди на {Place(i)}.").ToArray());
        var twoCategories = await BenchmarkAsync("uav+missile", Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? $"Шахеди на {Place(i)}." : $"Ракети на {Place(i)}.").ToArray());
        f.Evidence.Record("P09-W08", new { one_category = oneCategory, two_categories = twoCategories, note = "wall time of N observations through 2 track-worker replicas; category = partition (serial ceiling within one), (source, day) stats row shared by both" });
    }

    private static string Place(int i) => (i % 6) switch { 0 => "Сумщині", 1 => "Харківщині", 2 => "Полтавщині", 3 => "Чернігівщині", 4 => "Київщині", _ => "Дніпропетровщині" };

    private async Task<object> BenchmarkAsync(string label, string[] texts)
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var t0 = Recent(30);
        for (var i = 0; i < texts.Length; i++)
        {
            await f.Ingress.PublishAsync(f.Message($"bench-{label}-{i}", texts[i], t0.AddSeconds(i * 10)), source, "test", null, live: true, None);
        }
        var replica = f.NewConsumer(f.Services.GetRequiredService<TrackWriterHandler>(), "track-worker@replica-2");
        var sw = Stopwatch.StartNew();
        await StartAllAsync();
        await replica.StartAsync(None);
        long elapsedWriters;
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions") == texts.Length, TimeSpan.FromSeconds(120)));
            var writersStarted = sw.ElapsedMilliseconds;
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(120)));
            elapsedWriters = sw.ElapsedMilliseconds - writersStarted;
        }
        finally
        {
            await replica.StopAsync(None);
            await StopAllAsync();
        }
        var deadlocks = await f.CountAsync("processing.attempts", "subscription_id = 'track-worker' AND state = 'superseded' AND (error LIKE '%40P01%' OR error LIKE '%deadlock%')");
        return new
        {
            label,
            messages = texts.Length,
            total_ms = sw.ElapsedMilliseconds,
            writers_tail_ms = elapsedWriters,
            per_observation_ms = Math.Round((double)sw.ElapsedMilliseconds / texts.Length, 1),
            tracks = await f.CountAsync("target_tracks"),
            replica_split = new { primary = f.TrackWorker.Delivered, replica = replica.Delivered },
            deadlock_retries = deadlocks,
        };
    }
}
