using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Processing;
using Puluj.Processing.Incidents;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P14 (ADR-0005, plan §11): a replay run rebuilds a scope of raw messages in an isolated generation through the replay lane
/// while live keeps running and sees nothing of it; checkpoints are written with the batch they cover; verify → promote is one
/// atomic switch of the active generation and rollback switches back; a delta catchup pulls the raw messages that arrived meanwhile.
/// Every assert is on committed rows (`processing.runs/generations/stage_results`, `incidents`, `processing.deliveries`).
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class ReplayTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Actor = "operator:test";

    private static DateTimeOffset Recent(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

    private async Task StartAllAsync(bool replay)
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
        if (replay)
        {
            await f.Replay.StartAsync(None);
        }
    }

    private async Task StopAllAsync()
    {
        await f.Replay.StopAsync(None);
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

    private async Task PublishLiveAsync(string id, string text, DateTimeOffset at)
    {
        var source = await f.SourceAsync();
        var expected = await f.CountAsync("processing.extractions", "run_id IN (SELECT run_id FROM processing.runs WHERE kind = 'live')") + 1;
        await f.Ingress.PublishAsync(f.Message(id, text, at, sourceId: source.SourceId), source, "test", null, live: true, None);
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions", "run_id IN (SELECT run_id FROM processing.runs WHERE kind = 'live')") == expected, TimeSpan.FromSeconds(60)), $"live extraction {expected}");
        await SettleAsync();
    }

    /// <summary>Every expected delivery terminal and the outbox confirmed — for live and replay alike.</summary>
    private async Task SettleAsync()
    {
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(40)), "outbox confirmed");
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome IS NULL AND subscription_id <> 'message-analytics'") == 0, TimeSpan.FromSeconds(60)),
            "deliveries settled: " + await f.ScalarAsync<string>("SELECT COALESCE(string_agg(subscription_id || '/' || COALESCE(lane, '?') || ':' || n, ', '), '') FROM (SELECT subscription_id, lane, count(*) n FROM processing.deliveries WHERE outcome IS NULL GROUP BY 1, 2) d"));
    }

    private async Task<Guid> ActiveGenerationAsync() => await f.ScalarAsync<Guid>("SELECT generation_id FROM processing.generations WHERE is_active");

    /// <summary>An "API replica" (P11 pattern): every NOTIFY the map would receive — `IncidentChanged` from the projection, `TargetCreated` from the writers.</summary>
    private sealed class MapListener : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        public readonly ConcurrentBag<PulujEvent> Events = [];

        public MapListener(MessagingFixture f)
        {
            var listener = new PgNotifyListener(f.Services.GetRequiredService<IConfiguration>(), NullLogger<PgNotifyListener>.Instance);
            _pump = Task.Run(async () =>
            {
                await foreach (var evt in listener.ListenAsync(_cts.Token))
                {
                    if (evt.Type is PulujEventType.IncidentChanged or PulujEventType.TargetCreated or PulujEventType.TrackUpserted or PulujEventType.AlertChanged)
                    {
                        Events.Add(evt);
                    }
                }
            });
        }

        public int Count => Events.Count;

        /// <summary>The count once no new NOTIFY arrived for a second (a live post yields several: TargetCreated, IncidentChanged — the last may trail the receipts).</summary>
        public async Task<int> QuietCountAsync()
        {
            var last = Count;
            while (true)
            {
                await Task.Delay(1000);
                if (Count == last)
                {
                    return last;
                }
                last = Count;
            }
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
                // cancelled pump
            }
        }
    }

    private async Task<string> RunStateAsync(Guid runId) => await f.ScalarAsync<string>("SELECT state FROM processing.runs WHERE run_id = @id", ("id", runId));

    [Fact]
    public async Task R01_R03_R04_Shadow_replay_in_an_isolated_generation_then_atomic_promote_rollback_and_delta_catchup()
    {
        await f.ResetAsync();
        var scopeFrom = Recent(60);
        await using var map = new MapListener(f);
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE query ILIKE 'LISTEN %'") >= 1, TimeSpan.FromSeconds(20)), "listener up");
        await StartAllAsync(replay: false);
        try
        {
            // Live: two explosion reports → two incidents in the live generation, pushed to the map as usual (positive control for the NOTIFY count).
            await PublishLiveAsync("r01-a", "Вибухи у Харкові.", Recent(20));
            await PublishLiveAsync("r01-b", "Вибухи у Полтаві.", Recent(15));
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(map.Count >= 2), TimeSpan.FromSeconds(10)), "live NOTIFY reached the map listener");
            var notifiesBeforeReplay = await map.QuietCountAsync();
            var targetsBeforeReplay = await f.CountAsync("targets");
            var live = await ActiveGenerationAsync();
            Assert.Equal(IncidentStateWriter.LiveGeneration, live);
            Assert.Equal(2, await f.CountAsync("incidents", "generation_id = @g", ("g", live)));
            var liveRevisions = await f.ScalarAsync<string>("SELECT string_agg(incident_id || ':' || revision, ',' ORDER BY incident_id) FROM incidents");
            var liveProjection = await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome = 'completed'");
            Assert.True(liveProjection >= 2, "live incidents reached the projection");
            var llmCalls = await f.CountAsync("llm_requests");

            // A replay run over the same window: its own generation, nothing published until started.
            var scopeTo = DateTimeOffset.UtcNow;
            var runId = await f.Runs.CreateReplayAsync(new RunService.ReplayScope(null, scopeFrom, scopeTo, null), Actor, "rebuild incidents with the new catalog policy", None);
            var run = (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId);
            Assert.Equal(RunService.Created, run.State);
            Assert.Equal(2, run.Checkpoint!.Total);
            Assert.False(run.GenerationActive);
            var generation = run.GenerationId!.Value;
            Assert.NotEqual(live, generation);
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.PromoteAsync(runId, Actor, "too early", None)); // promote needs verified
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.PauseAsync(runId, Actor, "not running", None));

            await f.Runs.StartAsync(runId, Actor, "go", None);
            await f.Replay.StartAsync(None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("incidents", "generation_id = @g", ("g", generation)) == 2, TimeSpan.FromSeconds(90)),
                "replay incidents: " + await f.ScalarAsync<string>("SELECT COALESCE(string_agg(subscription_id || '/' || COALESCE(lane, '?') || '=' || COALESCE(outcome, 'pending') || ':' || n, ', '), '') FROM (SELECT subscription_id, lane, outcome, count(*) n FROM processing.deliveries GROUP BY 1, 2, 3) d"));
            await SettleAsync();
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!.Done, TimeSpan.FromSeconds(20)), "scope published");

            // Shadow, not production: live incidents and their revisions untouched, no `targets` row, no projection/track/alert deliveries in the
            // replay lane, no NOTIFY of any kind, no LLM call (parser skips the model outside live), stages ran once per raw in the replay run.
            Assert.Equal(liveRevisions, await f.ScalarAsync<string>("SELECT string_agg(incident_id || ':' || revision, ',' ORDER BY incident_id) FROM incidents WHERE generation_id = @g", ("g", live)));
            Assert.Equal(targetsBeforeReplay, await f.CountAsync("targets"));
            Assert.Equal(notifiesBeforeReplay, map.Count);
            Assert.Equal(0, await f.CountAsync("incident_observations", "generation_id = @g AND legacy_target_id IS NOT NULL", ("g", generation)));
            Assert.Equal(0, await f.CountAsync("processing.deliveries", "lane = 'replay' AND subscription_id IN ('projection', 'track-worker', 'alert-worker', 'raw-writer')"));
            Assert.Equal(liveProjection, await f.CountAsync("processing.deliveries", "subscription_id = 'projection' AND outcome = 'completed'"));
            Assert.Equal(2, await f.CountAsync("processing.deliveries", "lane = 'replay' AND subscription_id = 'incident-worker' AND outcome = 'completed'"));
            Assert.Equal(llmCalls, await f.CountAsync("llm_requests"));
            Assert.Equal(2, await f.CountAsync("processing.stage_results", "run_id = @r AND stage = 'normalize'", ("r", runId)));
            Assert.Equal(2, await f.CountAsync("processing.extractions", "run_id = @r", ("r", runId)));
            Assert.Equal(2, await f.CountAsync("messaging.events", "lane = 'replay' AND event_type = 'raw.stored' AND processing_run_id = @r", ("r", runId)));
            Assert.Equal(live, await ActiveGenerationAsync());

            // R04: raw messages that arrived while the replay ran — one fresh, one a late collector's post with an old published_at inside the
            // window — are outside the run until a catchup raises the ingest ceiling and extends the window to the watermark.
            await PublishLiveAsync("r04", "Вибухи у Сумах.", DateTimeOffset.UtcNow);
            await PublishLiveAsync("r04-late", "Вибухи у Дніпрі.", Recent(30)); // published before the scope end, stored after the run was created
            Assert.Equal(4, await f.CountAsync("incidents", "generation_id = @g", ("g", live)));
            Assert.Equal(2, await f.CountAsync("incidents", "generation_id = @g", ("g", generation)));
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(map.Count >= notifiesBeforeReplay + 2), TimeSpan.FromSeconds(10)), "the two live posts notified the map");
            var notifiesAfterLatePosts = await map.QuietCountAsync();
            await f.Runs.CatchUpAsync(runId, DateTimeOffset.UtcNow, Actor, "delta before promote", None); // watermark after the scope end and the fresh raw (≥ 1 s passed in QuietCountAsync)
            Assert.Equal(4, (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!.Total);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("incidents", "generation_id = @g", ("g", generation)) == 4, TimeSpan.FromSeconds(60)), "catchup replayed both late raws");
            await SettleAsync();
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!.Done, TimeSpan.FromSeconds(20)));
            Assert.Equal(4, (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!.Published);
            Assert.Equal(notifiesAfterLatePosts, map.Count); // the catchup replay notified nobody

            // R03: verify (all deliveries terminal) → promote = one atomic switch → the read side sees the new generation only.
            var report = await f.Runs.VerifyAsync(runId, Actor, "counts match", None);
            Assert.Equal(RunService.Verified, await RunStateAsync(runId));
            Assert.Equal(4, report["incidents_in_generation"]!.GetValue<long>());
            Assert.Equal(4, report["active_incidents_in_window"]!.GetValue<long>());
            Assert.Equal(0, report["pending_deliveries"]!.GetValue<long>());
            Assert.Equal(0, report["unanalyzed"]!.GetValue<long>());
            Assert.Equal(0, report["active_incidents_missing_in_generation"]!.GetValue<long>());
            Assert.Equal(0, report["active_incidents_outside_scope"]!.GetValue<long>());
            Assert.Equal(generation, await f.Runs.PromoteAsync(runId, Actor, "verified, switching", None));
            Assert.Equal(generation, await ActiveGenerationAsync());
            Assert.Equal(1, await f.CountAsync("processing.generations", "is_active"));
            Assert.Equal(4, await f.CountAsync("incidents i JOIN processing.generations g ON g.generation_id = i.generation_id", "g.is_active")); // IncidentQueries.Active predicate
            Assert.Equal(0, await f.CountAsync("incidents i JOIN processing.generations g ON g.generation_id = i.generation_id", "g.is_active AND i.generation_id = @g", ("g", live)));
            Assert.Equal(4, await f.CountAsync("incidents", "generation_id = @g", ("g", live))); // nothing deleted

            // Live after the promote writes into the promoted (active) generation — not into the old live one — and the map is notified.
            var notifiesBeforeLive = map.Count;
            await PublishLiveAsync("r03-live", "Вибухи в Одесі.", DateTimeOffset.UtcNow);
            Assert.Equal(5, await f.CountAsync("incidents", "generation_id = @g", ("g", generation)));
            Assert.Equal(4, await f.CountAsync("incidents", "generation_id = @g", ("g", live)));
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(map.Count > notifiesBeforeLive), TimeSpan.FromSeconds(10)), "the promoted generation is live for the map");

            // Rehearsed rollback: the previous generation is active again, the run is rolled_back, both result sets stay; what live wrote into the
            // promoted generation since the promote is counted in the audit (invisible until that window is replayed again).
            Assert.Equal(live, await f.Runs.RollbackAsync(runId, Actor, "rehearsal", None));
            Assert.Equal(live, await ActiveGenerationAsync());
            Assert.Equal(RunService.RolledBack, await RunStateAsync(runId));
            Assert.Equal(4, await f.CountAsync("incidents i JOIN processing.generations g ON g.generation_id = i.generation_id", "g.is_active"));
            Assert.Equal(5, await f.CountAsync("incidents", "generation_id = @g", ("g", generation)));
            Assert.Equal(1, await f.ScalarAsync<int>("SELECT (details->>'incidentsWrittenSincePromote')::int FROM messaging.control_audit WHERE action = 'run:rollback'"));
            await PublishLiveAsync("r03-after", "Вибухи у Львові.", DateTimeOffset.UtcNow); // live is back on the restored generation
            Assert.Equal(5, await f.CountAsync("incidents", "generation_id = @g", ("g", live)));
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.RollbackAsync(runId, Actor, "twice", None));
            var audit = await f.ScalarAsync<string>("SELECT string_agg(action, ',' ORDER BY audit_id) FROM messaging.control_audit WHERE action LIKE 'run:%'");
            Assert.Equal("run:create,run:start,run:catchup,run:verify,run:promote,run:rollback", audit);
            // A terminal run stays terminal; catchup after the promote is refused (the promoted generation is live's); a new replay run may be
            // opened once the previous one is terminal (one open run at a time) — and a verified run can be cancelled instead of promoted.
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.PromoteAsync(runId, Actor, "terminal", None));
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.CatchUpAsync(runId, DateTimeOffset.UtcNow.AddMinutes(5), Actor, "after rollback", None));
            var second = await f.Runs.CreateReplayAsync(new RunService.ReplayScope(null, scopeFrom, DateTimeOffset.UtcNow, null), Actor, "next attempt", None);
            await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.CreateReplayAsync(new RunService.ReplayScope(null, scopeFrom, DateTimeOffset.UtcNow, null), Actor, "third while second is open", None));
            await f.Runs.StartAsync(second, Actor, "go", None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == second).Checkpoint!.Done, TimeSpan.FromSeconds(60)), "second run published its scope");
            await SettleAsync();
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => { try { await f.Runs.VerifyAsync(second, Actor, "again", None); return true; } catch (RunConflictException) { return false; } }, TimeSpan.FromSeconds(30)), "second run verifies (leftovers of the first run do not block it)");
            await f.Runs.CancelAsync(second, Actor, "verified but not wanted", None); // review N1: a verified run can be cancelled
            Assert.Equal(RunService.Cancelled, await RunStateAsync(second));
            f.Evidence.Record("P14-R01-R03-R04", new { runId, live, generation, liveRevisions, report = report.ToJsonString(), audit });
        }
        finally
        {
            await StopAllAsync();
        }
    }

    [Fact]
    public async Task R02_Checkpoints_are_written_with_their_batch_pause_cancel_are_honoured_and_a_repeated_batch_is_a_noop_downstream()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var t0 = Recent(30);
        for (var i = 0; i < 5; i++)
        {
            await f.ExecAsync(
                "INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, hash, processing_status, attempts) VALUES (@s, @m, @m, '0', @at, now(), @t, md5(@t), 0, 0)",
                ("s", source.SourceId), ("m", $"r02-{i}"), ("at", t0.AddMinutes(i)), ("t", $"Повідомлення {i} без цілей."));
        }
        var runId = await f.Runs.CreateReplayAsync(new RunService.ReplayScope([source.SourceId], t0.AddMinutes(-1), t0.AddMinutes(10), null), Actor, "checkpoint test", None);
        Assert.False(await f.Replay.PublishOnceAsync(None)); // created: nothing to do
        await f.Runs.StartAsync(runId, Actor, "go", None);

        Assert.True(await f.Replay.PublishOnceAsync(None)); // batch of 2
        var cp = (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!;
        Assert.Equal((2, 5, false), (cp.Published, cp.Total, cp.Done));
        Assert.Equal(2, await f.CountAsync("messaging.outbox", "lane = 'replay' AND event_type = 'raw.stored'"));

        await f.Runs.PauseAsync(runId, Actor, "hold", None);
        Assert.False(await f.Replay.PublishOnceAsync(None)); // paused: the lease finds no running run
        Assert.Equal(2, await f.CountAsync("messaging.outbox", "lane = 'replay'"));
        await f.Runs.ResumeAsync(runId, Actor, "continue", None);
        Assert.True(await f.Replay.PublishOnceAsync(None));
        Assert.True(await f.Replay.PublishOnceAsync(None));
        Assert.False(await f.Replay.PublishOnceAsync(None)); // scope exhausted → done
        cp = (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).Checkpoint!;
        Assert.Equal((5, 5, true), (cp.Published, cp.Total, cp.Done));
        Assert.Equal(5, await f.CountAsync("messaging.outbox", "lane = 'replay' AND event_type = 'raw.stored'"));
        Assert.Equal(5, await f.ScalarAsync<long>("SELECT count(DISTINCT (envelope->>'raw_message_id')) FROM messaging.outbox WHERE lane = 'replay'")); // each raw once

        // A crash between the commit of a batch and the next read cannot lose or duplicate work; a repeated batch (checkpoint rewound by hand,
        // as after a restored backup) publishes the last two raws again with new event ids — downstream the per-run stage uniqueness makes them noops.
        await f.ExecAsync("UPDATE processing.runs SET checkpoint = jsonb_set(jsonb_set(jsonb_set(checkpoint, '{done}', 'false'), '{published}', '3'), '{lastRawMessageId}', to_jsonb((SELECT raw_message_id FROM raw_messages WHERE source_message_id = 'r02-2'))) WHERE run_id = @id", ("id", runId));
        Assert.True(await f.Replay.PublishOnceAsync(None));
        Assert.Equal(7, await f.CountAsync("messaging.outbox", "lane = 'replay' AND event_type = 'raw.stored'"));
        await f.Relay.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Archive.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND lane = 'replay' AND outcome IS NOT NULL") == 7, TimeSpan.FromSeconds(40)), "normalizer receipts");
            Assert.Equal(5, await f.CountAsync("processing.stage_results", "run_id = @r AND stage = 'normalize'", ("r", runId)));
            Assert.Equal(2, await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND lane = 'replay' AND outcome = 'noop'"));
        }
        finally
        {
            await f.Archive.StopAsync(None);
            await f.Normalizer.StopAsync(None);
            await f.Relay.StopAsync(None);
        }
        await f.Runs.CancelAsync(runId, Actor, "enough", None);
        Assert.Equal(RunService.Cancelled, await RunStateAsync(runId));
        Assert.False(await f.Replay.PublishOnceAsync(None));
        await Assert.ThrowsAsync<RunConflictException>(() => f.Runs.ResumeAsync(runId, Actor, "cancelled is terminal", None));
        f.Evidence.Record("P14-R02", new { runId, batches = f.Replay.Batches, published = 5, repeated = 2, normalize_rows = 5, normalizer_noops = 2 });
    }

    [Fact]
    public async Task R06_Promote_waits_for_a_writer_that_holds_the_active_generation_and_the_next_writer_sees_the_new_one()
    {
        await f.ResetAsync();
        await f.ExecAsync("INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES (@g, true, now())", ("g", IncidentStateWriter.LiveGeneration)); // what the first live incident would have created
        // An empty scope verifies at once: the run only serves as the thing to promote.
        var runId = await f.Runs.CreateReplayAsync(new RunService.ReplayScope(null, Recent(120), Recent(90), null), Actor, "lock test", None);
        await f.Runs.StartAsync(runId, Actor, "go", None);
        Assert.False(await f.Replay.PublishOnceAsync(None)); // nothing in scope → done
        await f.Runs.VerifyAsync(runId, Actor, "empty scope verifies", None);
        var generation = (await f.Runs.ListAsync(10, None)).Single(r => r.RunId == runId).GenerationId!.Value;

        // Writer A: reads the active generation (shared lock) and keeps its transaction open.
        await using var dbA = await f.Factory.CreateDbContextAsync();
        var connA = (Npgsql.NpgsqlConnection)dbA.Database.GetDbConnection();
        await connA.OpenAsync();
        await using var txA = await connA.BeginTransactionAsync();
        var seenByA = await IncidentStateWriter.EnsureGenerationAsync(connA, txA, null, None);
        Assert.Equal(IncidentStateWriter.LiveGeneration, seenByA);

        // Promote must wait for A (exclusive lock), not commit underneath it.
        var promote = f.Runs.PromoteAsync(runId, Actor, "switch", None);
        await Task.Delay(1500);
        Assert.False(promote.IsCompleted, "promote committed while a writer still held the active generation");
        Assert.Equal(IncidentStateWriter.LiveGeneration, await ActiveGenerationAsync());
        await txA.CommitAsync();
        Assert.Equal(generation, await promote.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(generation, await ActiveGenerationAsync());

        // Writer B after the promote stamps the promoted generation.
        await using var dbB = await f.Factory.CreateDbContextAsync();
        var connB = (Npgsql.NpgsqlConnection)dbB.Database.GetDbConnection();
        await connB.OpenAsync();
        await using var txB = await connB.BeginTransactionAsync();
        Assert.Equal(generation, await IncidentStateWriter.EnsureGenerationAsync(connB, txB, null, None));
        await txB.RollbackAsync();
        await f.Runs.RollbackAsync(runId, Actor, "cleanup", None);
        Assert.Equal(IncidentStateWriter.LiveGeneration, await ActiveGenerationAsync());
        f.Evidence.Record("P14-R06", new { runId, generation, promote_waited_ms = ">=1500" });
    }

    [Fact]
    public async Task R05_A_newer_pipeline_version_supersedes_the_open_live_run()
    {
        await f.ResetAsync();
        await using var db = await f.Factory.CreateDbContextAsync();
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        var current = await f.Outbox.Runs.GetOpenRunAsync(conn, null, "live", None);
        var newer = new ProcessingRuns("p14-next", "test");
        var next = await newer.GetOpenRunAsync(conn, null, "live", None);
        Assert.NotEqual(current, next);
        Assert.Equal("superseded", await f.ScalarAsync<string>("SELECT state FROM processing.runs WHERE run_id = @id", ("id", current)));
        Assert.Equal(1, await f.CountAsync("processing.runs", "run_id = @id AND finished_at IS NOT NULL", ("id", current)));
        Assert.Equal(current, await f.ScalarAsync<Guid>("SELECT supersedes_run_id FROM processing.runs WHERE run_id = @id", ("id", next)));
        Assert.Equal("p14-next", await f.ScalarAsync<string>("SELECT versions->>'pipeline_version' FROM processing.runs WHERE run_id = @id", ("id", next)));
        Assert.Equal(1, await f.CountAsync("processing.runs", "lane = 'live' AND state = 'running'"));
        Assert.Equal(next, await newer.GetOpenRunAsync(conn, null, "live", None)); // cached, stable
        Assert.Equal(current, await f.Outbox.Runs.GetOpenRunAsync(conn, null, "live", None)); // the older process keeps its cached run until restart
        f.Outbox.Runs.Reset();
        // An older process (started before the newer run was opened) does not supersede it back: the newest deploy owns the lane (review N1).
        Assert.Equal(next, await f.Outbox.Runs.GetOpenRunAsync(conn, null, "live", None));
        Assert.Equal(1, await f.CountAsync("processing.runs", "lane = 'live' AND state = 'running'"));
        // A process started after that run with yet another version supersedes it.
        var newest = new ProcessingRuns("p14-next-2", "test", DateTimeOffset.UtcNow.AddSeconds(5));
        var third = await newest.GetOpenRunAsync(conn, null, "live", None);
        Assert.Equal(next, await f.ScalarAsync<Guid>("SELECT supersedes_run_id FROM processing.runs WHERE run_id = @id", ("id", third)));
        f.Outbox.Runs.Reset();
        f.Evidence.Record("P14-R05", new { superseded = current, next });
    }
}
