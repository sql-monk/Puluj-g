using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Puluj.Infrastructure.Processing;
using Puluj.Processing.Analytics;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P15 (ADR-0013): the lifecycle projection is fed by the `message-analytics` subscription — one row per raw message per run,
/// the root counted once (edits and repeated `raw.stored` land on the same key), analysis and domain fields filled by events
/// that may arrive in any order, no-text and failed messages present with their outcome, a replay run in its own row.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class LifecycleTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private async Task StartAllAsync()
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

    private async Task StopAllAsync()
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

    private async Task SettleAsync(string what)
    {
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(40)), $"{what}: outbox confirmed");
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "outcome IS NULL") == 0, TimeSpan.FromSeconds(60)),
            $"{what}: deliveries settled: " + await f.ScalarAsync<string>("SELECT COALESCE(string_agg(subscription_id || '/' || COALESCE(lane, '?') || ':' || n, ', '), '') FROM (SELECT subscription_id, lane, count(*) n FROM processing.deliveries WHERE outcome IS NULL GROUP BY 1, 2) d"));
    }

    private sealed record Row(Guid RunId, string Lane, bool HasText, int TextLength, bool IsEdit, DateTimeOffset? StoredAt, DateTimeOffset? AnalyzedAt, string? Outcome, string? Method, int FactCount, int Unlocated,
        bool TimingsAvailable, string? Timings, string? Error, string[] Expected, string[] Done, DateTimeOffset? DomainCompletedAt, long[] IncidentIds, long[] TrackIds, string SourceOfTruth);

    private async Task<List<Row>> RowsAsync(long rawId)
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT run_id, lane, has_text, text_length, is_edit, stored_at, analyzed_at, analysis_outcome, method, fact_count, unlocated_facts, timings_available, timings::text, error, expected_branches, branches_done, domain_completed_at, incident_ids, track_ids, source_of_truth FROM analytics.message_lifecycle WHERE raw_message_id = @id ORDER BY updated_at", conn);
        cmd.Parameters.AddWithValue("id", rawId);
        var rows = new List<Row>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3), reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetBoolean(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetFieldValue<string[]>(14), reader.GetFieldValue<string[]>(15),
                reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16), reader.GetFieldValue<long[]>(17), reader.GetFieldValue<long[]>(18), reader.GetString(19)));
        }
        return rows;
    }

    [Fact]
    public async Task L01_Lifecycle_rows_follow_the_events_root_once_no_text_and_failed_visible_replay_in_its_own_row()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        await StartAllAsync();
        try
        {
            // A report with an incident and a target: root → analysis (rules, 2 facts) → domain (incident + track) → completed.
            await f.Ingress.PublishAsync(f.Message("l01-a", "Шахеди на Харківщині. Вибухи у Харкові.", DateTimeOffset.UtcNow.AddMinutes(-5), sourceId: source.SourceId), source, "test", null, live: true, None);
            // A structured (no-text) message and an unsupported one: visible with their outcome, not hidden.
            await f.Ingress.PublishAsync(f.Message("l01-b", null, DateTimeOffset.UtcNow.AddMinutes(-4), sourceId: source.SourceId) with { RawPayload = JsonDocument.Parse("""{"kind":"structured"}""") }, source, "test", null, live: true, None);
            await f.Ingress.PublishAsync(f.Message("l01-c", "Добрий ранок, друзі! Гарного дня.", DateTimeOffset.UtcNow.AddMinutes(-3), sourceId: source.SourceId), source, "test", null, live: true, None);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("raw_messages") == 3, TimeSpan.FromSeconds(30)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions") == 3, TimeSpan.FromSeconds(60)), "three analyses");
            await SettleAsync("live");

            var a = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages WHERE source_message_id = 'l01-a'");
            var rowA = Assert.Single(await RowsAsync(a));
            Assert.Equal("live", rowA.Lane);
            Assert.True(rowA.HasText);
            Assert.NotNull(rowA.StoredAt);
            Assert.NotNull(rowA.AnalyzedAt);
            Assert.Equal("completed", rowA.Outcome);
            Assert.Equal("rules", rowA.Method);
            Assert.Equal(2, rowA.FactCount);
            Assert.True(rowA.TimingsAvailable);
            Assert.Contains("parsed_at", rowA.Timings!);
            Assert.Equal(["incident-worker", "track-worker"], rowA.Expected.Order());
            Assert.Equal(["incident-worker", "track-worker"], rowA.Done.Order());
            Assert.NotNull(rowA.DomainCompletedAt);
            Assert.Single(rowA.IncidentIds);
            Assert.Single(rowA.TrackIds);
            Assert.Equal("event", rowA.SourceOfTruth);
            Assert.Equal(3, await f.CountAsync("processing.deliveries", "subscription_id = 'message-analytics' AND outcome = 'completed' AND reason = 'root'")); // one root receipt per raw
            Assert.True(await f.CountAsync("processing.deliveries", "subscription_id = 'message-analytics' AND outcome = 'completed' AND reason = 'domain'") >= 2);

            var b = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages WHERE source_message_id = 'l01-b'");
            var rowB = Assert.Single(await RowsAsync(b));
            Assert.False(rowB.HasText);
            Assert.NotNull(rowB.Outcome); // no-text is analyzed (unsupported/no_facts), not invisible
            Assert.Equal(0, rowB.FactCount);
            Assert.NotNull(rowB.DomainCompletedAt); // nothing expected → complete at analysis
            var c = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages WHERE source_message_id = 'l01-c'");
            var rowC = Assert.Single(await RowsAsync(c));
            Assert.Equal("no_facts", rowC.Outcome);
            Assert.Equal(3, await f.ScalarAsync<long>("SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle"));

            // A failed analysis is visible as `failed` with its error, and a later result (any order) lands in the same row: a synthetic
            // message.analysis.completed{failed} for raw c published straight to the broker (new event id, later occurred_at).
            var analysisC = await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'message.analysis.completed' AND (envelope->>'raw_message_id')::bigint = @id", ("id", c));
            var failed = System.Text.Json.Nodes.JsonNode.Parse(analysisC)!.AsObject();
            failed["event_id"] = Guid.CreateVersion7().ToString();
            failed["occurred_at"] = DateTimeOffset.UtcNow.ToString("O");
            failed["payload"]!["outcome"] = "failed";
            failed["payload"]!["fact_count"] = 0;
            failed["payload"]!["error"] = new System.Text.Json.Nodes.JsonObject { ["code"] = "parse_failed", ["message"] = "boom: parser crashed" };
            await f.PublishRawAsync("puluj.live.message.analysis.completed", System.Text.Encoding.UTF8.GetBytes(failed.ToJsonString()), failed["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await RowsAsync(c)).Single().Outcome == "failed", TimeSpan.FromSeconds(20)), "late failed analysis visible");
            Assert.Contains("boom: parser crashed", (await RowsAsync(c)).Single().Error!);

            // A repeated raw.stored for the same raw (a redelivery / an edit bridging) does not create a second root.
            var stored = await f.ScalarAsync<Guid>("SELECT event_id FROM messaging.outbox WHERE event_type = 'raw.stored' AND (envelope->>'raw_message_id')::bigint = @id", ("id", a));
            var body = await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_id = @e", ("e", stored));
            var replayed = body.Replace(stored.ToString(), Guid.CreateVersion7().ToString());
            await f.PublishRawAsync("puluj.live.raw.stored", System.Text.Encoding.UTF8.GetBytes(replayed), Guid.CreateVersion7().ToString());
            await Task.Delay(1500);
            Assert.Single(await RowsAsync(a));
            Assert.Equal(3, await f.ScalarAsync<long>("SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle"));

            // Out-of-order: an analysis that reaches the projection before its raw.stored (raw d inserted straight into the table, no root event yet)
            // opens the row; the root that follows fills stored_at without losing the analysis (review N7).
            await f.ExecAsync("""
                INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, raw_text, raw_payload, hash, processing_status, attempts)
                VALUES (@s, 'l01-d', 'l01-d', '0', now() - interval '2 minutes', now() - interval '110 seconds', 'Вибухи у Сумах.', '{}'::jsonb, 'l01-d-hash', 0, 0)
                """, ("s", source.SourceId));
            var d = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages WHERE source_message_id = 'l01-d'");
            var early = System.Text.Json.Nodes.JsonNode.Parse(analysisC)!.AsObject();
            early["event_id"] = Guid.CreateVersion7().ToString();
            early["occurred_at"] = DateTimeOffset.UtcNow.ToString("O");
            early["raw_message_id"] = d;
            early["payload"]!["raw_message_id"] = d;
            await f.PublishRawAsync("puluj.live.message.analysis.completed", System.Text.Encoding.UTF8.GetBytes(early.ToJsonString()), early["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await RowsAsync(d)).Count == 1, TimeSpan.FromSeconds(20)), "analysis-first row");
            var rowD = (await RowsAsync(d)).Single();
            Assert.Null(rowD.StoredAt);
            Assert.NotNull(rowD.AnalyzedAt);
            Assert.Equal("no_facts", rowD.Outcome);
            var rootD = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
            rootD["event_id"] = Guid.CreateVersion7().ToString();
            rootD["occurred_at"] = DateTimeOffset.UtcNow.ToString("O");
            rootD["raw_message_id"] = d;
            rootD["payload"]!["raw_message_id"] = d;
            await f.PublishRawAsync("puluj.live.raw.stored", System.Text.Encoding.UTF8.GetBytes(rootD.ToJsonString()), rootD["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await RowsAsync(d)).SingleOrDefault()?.StoredAt is not null, TimeSpan.FromSeconds(20)), "root after analysis");
            rowD = (await RowsAsync(d)).Single();
            Assert.NotNull(rowD.AnalyzedAt); // the root upsert keeps the analysis fields
            Assert.NotNull(rowD.Outcome);
            Assert.Equal(4, await f.ScalarAsync<long>("SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle"));

            // A replay run of raw a: its own (raw, run) row, the live row untouched.
            var runId = await f.Runs.CreateReplayAsync(new RunService.ReplayScope([source.SourceId], DateTimeOffset.UtcNow.AddMinutes(-6), DateTimeOffset.UtcNow.AddMinutes(-4).AddSeconds(30), null), "operator:test", "lifecycle replay", None);
            await f.Runs.StartAsync(runId, "operator:test", "go", None);
            Assert.True(await f.Replay.PublishOnceAsync(None));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => (await RowsAsync(a)).Count == 2, TimeSpan.FromSeconds(60)), "replay row");
            await SettleAsync("replay");
            var rows = await RowsAsync(a);
            var live = rows.Single(r => r.RunId != runId);
            var replay = rows.Single(r => r.RunId == runId);
            Assert.Equal(rowA.AnalyzedAt, live.AnalyzedAt);
            Assert.Equal("replay", replay.Lane);
            Assert.Equal("completed", replay.Outcome);
            Assert.Equal(["incident-worker"], replay.Expected); // track-worker does not serve the replay lane (P14)
            Assert.Single(replay.IncidentIds);
            Assert.Empty(replay.TrackIds);
            Assert.NotNull(replay.DomainCompletedAt);
            Assert.Equal(4, await f.ScalarAsync<long>("SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle")); // roots once, results per run
            await f.Runs.CancelAsync(runId, "operator:test", "done", None);
            f.Evidence.Record("P15-L01", new { rows = rows.Count, live = new { live.Outcome, live.FactCount, live.Expected, live.Done }, replay = new { replay.Outcome, replay.Expected, replay.Done }, noText = new { rowB.HasText, rowB.Outcome }, analysisFirst = new { rowD.StoredAt, rowD.Outcome }, roots = 4 });
        }
        finally
        {
            await StopAllAsync();
        }
    }
}
