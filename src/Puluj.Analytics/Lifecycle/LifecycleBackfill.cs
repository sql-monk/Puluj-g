using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Lifecycle;

/// <summary>
/// Rebuilds `analytics.message_lifecycle` from durable evidence (plan §10, ADR-0013, P15) in raw-id order with a resumable cursor
/// (`analytics.state lifecycle_backfill_cursor`): the root from `raw_messages`; analysis from the stage tables (`processing.extractions`
/// for the outcome/method/versions, `processing.stage_results` for the timings, `processing.observations` for facts); the domain from
/// `processing.deliveries` over the archived events (completion) and from `incident_observations`/`track_targets`/`air_alerts` (ids);
/// the cost from `llm_requests`. A raw processed before the stage tables existed (no extraction) is filed under the synthetic
/// `legacy` run with the outcome derived from `processing_status`, `timings_available = false` and `completion_available = false` —
/// unknown is written as unknown, never invented. Event-sourced rows are never overwritten: the backfill only fills what is NULL.
/// </summary>
public sealed class LifecycleBackfill(IDbContextFactory<AnalyticsDbContext> factory, ILogger<LifecycleBackfill> logger)
{
    public const string CursorKey = "lifecycle_backfill_cursor";
    public const string ReportKey = "lifecycle_backfill_report";
    /// <summary>`UUIDv5(run:legacy)` — the same value `MessageAnalyticsHandler.LegacyRun` uses (Processing is not referenced here).</summary>
    public static readonly Guid LegacyRun = new("18f077f6-6906-56e9-b3ae-fd9141ab4292");

    public sealed record Progress(long Cursor, long MaxRawMessageId, long Processed, long LegacyRows, long EventRowsKept, DateTimeOffset At);

    /// <summary>A raw younger than this is left to the events (the consumer may simply not have projected it yet); only older raws without any platform trace count as legacy.</summary>
    public static readonly TimeSpan LegacyGrace = TimeSpan.FromMinutes(10);

    /// <summary>Processes up to <paramref name="maxBatches"/> batches from the cursor; returns the progress (Cursor == MaxRawMessageId when caught up).</summary>
    public async Task<Progress> RunAsync(int batchSize, int maxBatches, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        // One backfill at a time (the worker loop and an operator's POST share the cursor): a session advisory lock, released with the connection.
        await using (var gate = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('lifecycle_backfill'))", conn))
        {
            if (!(bool)(await gate.ExecuteScalarAsync(ct))!)
            {
                var busy = await CursorAsync(conn, ct);
                logger.LogInformation("Lifecycle backfill: another pass holds the lock, skipped (cursor {Cursor})", busy);
                return new Progress(busy, busy, 0, 0, 0, DateTimeOffset.UtcNow);
            }
        }
        var cursor = await CursorAsync(conn, ct);
        long max;
        await using (var m = new NpgsqlCommand("SELECT coalesce(max(raw_message_id), 0) FROM raw_messages", conn))
        {
            max = (long)(await m.ExecuteScalarAsync(ct))!;
        }
        long processed = 0, legacy = 0, kept = 0;
        for (var i = 0; i < maxBatches && cursor < max; i++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var (last, rows, legacyRows, eventRows) = await BackfillBatchAsync(conn, tx, cursor, batchSize, ct);
            if (rows == 0)
            {
                cursor = max;
            }
            else
            {
                cursor = last;
            }
            await SaveCursorAsync(conn, tx, cursor, ct);
            await tx.CommitAsync(ct);
            processed += rows;
            legacy += legacyRows;
            kept += eventRows;
        }
        var progress = new Progress(cursor, max, processed, legacy, kept, DateTimeOffset.UtcNow);
        logger.LogInformation("Lifecycle backfill: cursor {Cursor}/{Max}, +{Rows} rows ({Legacy} legacy, {Kept} event rows kept)", cursor, max, processed, legacy, kept);
        return progress;
    }

    public static async Task<long> CursorAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT value FROM analytics.state WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", CursorKey);
        return await cmd.ExecuteScalarAsync(ct) is string s && long.TryParse(s, out var v) ? v : 0;
    }

    private static async Task SaveCursorAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long cursor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO analytics.state (key, value, updated_at) VALUES (@k, @v, now()) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()", conn, tx);
        cmd.Parameters.AddWithValue("k", CursorKey);
        cmd.Parameters.AddWithValue("v", cursor.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Resets the cursor (a full rebuild on the next passes); rows are upserted, nothing is deleted.</summary>
    public async Task ResetCursorAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SaveCursorAsync(conn, tx, 0, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// One batch: every raw with `raw_message_id > cursor` (ordered) becomes a row per run that has an extraction for it, or a `legacy` row
    /// when none exists. Returns (last raw id, rows written, legacy rows, event rows left untouched).
    /// </summary>
    public static async Task<(long Last, long Rows, long Legacy, long Kept)> BackfillBatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long cursor, int batchSize, CancellationToken ct)
    {
        long last;
        await using (var range = new NpgsqlCommand("SELECT coalesce(max(raw_message_id), @cursor) FROM (SELECT raw_message_id FROM raw_messages WHERE raw_message_id > @cursor ORDER BY raw_message_id LIMIT @batch) b", conn, tx))
        {
            range.Parameters.AddWithValue("cursor", cursor);
            range.Parameters.AddWithValue("batch", batchSize);
            last = (long)(await range.ExecuteScalarAsync(ct))!;
        }
        if (last == cursor)
        {
            return (cursor, 0, 0, 0);
        }
        var (rows, legacy, kept) = await BackfillRangeAsync(conn, tx, "r.raw_message_id > @cursor AND r.raw_message_id <= @last", cmd =>
        {
            cmd.Parameters.AddWithValue("cursor", cursor);
            cmd.Parameters.AddWithValue("last", last);
        }, LegacyGrace, ct);
        return (last, rows, legacy, kept);
    }

    /// <summary>Backfill of an explicit id set (the reconciliation's missing roots): the same statements over `raw_message_id = ANY(@ids)`.</summary>
    public static async Task<(long Rows, long Legacy, long Kept)> BackfillIdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<long> ids, TimeSpan grace, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return (0, 0, 0);
        }
        return await BackfillRangeAsync(conn, tx, "r.raw_message_id = ANY(@ids)", cmd => cmd.Parameters.AddWithValue("ids", ids.ToArray()), grace, ct);
    }

    private static async Task<(long Rows, long Legacy, long Kept)> BackfillRangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string filter, Action<NpgsqlCommand> bind, TimeSpan grace, CancellationToken ct)
    {
        // 1. Rows with stage evidence: one per (raw, run) that has an extraction. Event-sourced rows keep every field (COALESCE with the existing value).
        long stageRows;
        await using (var cmd = new NpgsqlCommand(
            $$"""
            INSERT INTO analytics.message_lifecycle (raw_message_id, run_id, source_id, source_message_key, source_revision, lane, published_at, received_at, stored_at,
                has_text, text_length, has_payload, is_edit, analyzed_at, analysis_outcome, method, fact_count, unlocated_facts, versions, timings, timings_available, error,
                expected_branches, branches_done, domain_completed_at, completion_available, incident_ids, track_ids, alert_ids,
                llm_calls, llm_input_tokens, llm_cache_tokens, llm_output_tokens, llm_cost_usd, llm_latency_ms, generation_id, source_of_truth, updated_at)
            SELECT r.raw_message_id, x.run_id, r.source_id, r.source_message_key, r.source_revision, coalesce(pr.lane, 'live'), r.published_at, r.received_at,
                   COALESCE((SELECT e.occurred_at FROM messaging.events e WHERE e.raw_message_id = r.raw_message_id AND e.processing_run_id = x.run_id AND e.event_type = 'raw.stored' ORDER BY e.occurred_at LIMIT 1),
                            (SELECT min(sr.started_at) FROM processing.stage_results sr WHERE sr.raw_message_id = r.raw_message_id AND sr.run_id = x.run_id)),
                   r.raw_text IS NOT NULL AND r.raw_text <> '', coalesce(length(r.raw_text), 0), r.raw_payload IS NOT NULL, r.source_revision <> '0',
                   x.created_at, x.outcome, x.method, (SELECT count(*)::int FROM processing.observations o WHERE o.extraction_id = x.extraction_id),
                   (SELECT count(*)::int FROM processing.observations o WHERE o.extraction_id = x.extraction_id AND (o.payload->'location' IS NULL OR o.payload->'location' = 'null'::jsonb)),
                   x.versions,
                   (SELECT jsonb_object_agg(sr.stage || '_at', sr.finished_at) FROM processing.stage_results sr WHERE sr.raw_message_id = r.raw_message_id AND sr.run_id = x.run_id AND sr.finished_at IS NOT NULL),
                   EXISTS (SELECT 1 FROM processing.stage_results sr WHERE sr.raw_message_id = r.raw_message_id AND sr.run_id = x.run_id),
                   left(x.error::text, 2000),
                   coalesce((SELECT array_agg(DISTINCT d.subscription_id) FROM messaging.events e JOIN processing.deliveries d ON d.event_id = e.event_id
                             WHERE e.raw_message_id = r.raw_message_id AND e.processing_run_id = x.run_id AND e.event_type = 'observations.recorded' AND d.subscription_id IN ('track-worker', 'alert-worker', 'incident-worker')), '{}'),
                   '{}',
                   COALESCE((SELECT CASE WHEN count(*) FILTER (WHERE d.outcome IS NULL) = 0 THEN max(d.completed_at) END FROM messaging.events e JOIN processing.deliveries d ON d.event_id = e.event_id
                     WHERE e.raw_message_id = r.raw_message_id AND e.processing_run_id = x.run_id AND e.event_type = 'observations.recorded' AND d.subscription_id IN ('track-worker', 'alert-worker', 'incident-worker')),
                     CASE WHEN NOT EXISTS (SELECT 1 FROM messaging.events e2 WHERE e2.raw_message_id = r.raw_message_id AND e2.processing_run_id = x.run_id AND e2.event_type = 'observations.recorded')
                               AND EXISTS (SELECT 1 FROM messaging.events e3 WHERE e3.raw_message_id = r.raw_message_id AND e3.processing_run_id = x.run_id) THEN x.created_at END),
                   EXISTS (SELECT 1 FROM messaging.events e WHERE e.raw_message_id = r.raw_message_id AND e.processing_run_id = x.run_id),
                   coalesce((SELECT array_agg(DISTINCT io.incident_id) FROM incident_observations io JOIN processing.observations o ON o.observation_id = io.observation_id WHERE o.extraction_id = x.extraction_id), '{}'),
                   coalesce((SELECT array_agg(DISTINCT tt.target_track_id) FROM targets t JOIN track_targets tt ON tt.target_id = t.target_id WHERE t.raw_message_id = r.raw_message_id AND t.observation_id IN (SELECT observation_id FROM processing.observations o WHERE o.extraction_id = x.extraction_id)), '{}'),
                   coalesce((SELECT array_agg(DISTINCT a.air_alert_id) FROM air_alerts a WHERE a.start_raw_message_id = r.raw_message_id OR a.end_raw_message_id = r.raw_message_id), '{}'),
                   coalesce(l.calls, 0), coalesce(l.input, 0), coalesce(l.cache, 0), coalesce(l.output, 0), coalesce(l.cost, 0), coalesce(l.latency, 0),
                   (SELECT i.generation_id FROM incident_observations io JOIN processing.observations o ON o.observation_id = io.observation_id JOIN incidents i ON i.incident_id = io.incident_id WHERE o.extraction_id = x.extraction_id LIMIT 1),
                   'backfill', now()
            FROM raw_messages r
            JOIN processing.extractions x ON x.raw_message_id = r.raw_message_id
            LEFT JOIN processing.runs pr ON pr.run_id = x.run_id
            LEFT JOIN LATERAL (SELECT count(*)::int calls, sum(input_tokens) input, sum(coalesce(cache_read_input_tokens, 0) + coalesce(cache_creation_input_tokens, 0)) cache, sum(output_tokens) output, sum(estimated_cost_usd) cost, sum(duration_ms)::int latency
                               FROM llm_requests q WHERE q.raw_message_id = r.raw_message_id AND q.run_id = x.run_id) l ON true
            WHERE {{filter}}
            ON CONFLICT (raw_message_id, run_id) DO UPDATE SET
                analyzed_at = COALESCE(analytics.message_lifecycle.analyzed_at, EXCLUDED.analyzed_at),
                analysis_outcome = COALESCE(analytics.message_lifecycle.analysis_outcome, EXCLUDED.analysis_outcome),
                method = COALESCE(analytics.message_lifecycle.method, EXCLUDED.method),
                fact_count = CASE WHEN analytics.message_lifecycle.analyzed_at IS NULL THEN EXCLUDED.fact_count ELSE analytics.message_lifecycle.fact_count END,
                unlocated_facts = CASE WHEN analytics.message_lifecycle.analyzed_at IS NULL THEN EXCLUDED.unlocated_facts ELSE analytics.message_lifecycle.unlocated_facts END,
                versions = COALESCE(analytics.message_lifecycle.versions, EXCLUDED.versions),
                timings = COALESCE(analytics.message_lifecycle.timings, EXCLUDED.timings),
                timings_available = analytics.message_lifecycle.timings_available OR EXCLUDED.timings_available,
                error = COALESCE(analytics.message_lifecycle.error, EXCLUDED.error),
                expected_branches = CASE WHEN cardinality(analytics.message_lifecycle.expected_branches) = 0 THEN EXCLUDED.expected_branches ELSE analytics.message_lifecycle.expected_branches END,
                domain_completed_at = COALESCE(analytics.message_lifecycle.domain_completed_at, EXCLUDED.domain_completed_at),
                incident_ids = CASE WHEN cardinality(analytics.message_lifecycle.incident_ids) = 0 THEN EXCLUDED.incident_ids ELSE analytics.message_lifecycle.incident_ids END,
                track_ids = CASE WHEN cardinality(analytics.message_lifecycle.track_ids) = 0 THEN EXCLUDED.track_ids ELSE analytics.message_lifecycle.track_ids END,
                alert_ids = CASE WHEN cardinality(analytics.message_lifecycle.alert_ids) = 0 THEN EXCLUDED.alert_ids ELSE analytics.message_lifecycle.alert_ids END,
                llm_calls = GREATEST(analytics.message_lifecycle.llm_calls, EXCLUDED.llm_calls),
                llm_input_tokens = GREATEST(analytics.message_lifecycle.llm_input_tokens, EXCLUDED.llm_input_tokens),
                llm_cache_tokens = GREATEST(analytics.message_lifecycle.llm_cache_tokens, EXCLUDED.llm_cache_tokens),
                llm_output_tokens = GREATEST(analytics.message_lifecycle.llm_output_tokens, EXCLUDED.llm_output_tokens),
                llm_cost_usd = GREATEST(analytics.message_lifecycle.llm_cost_usd, EXCLUDED.llm_cost_usd),
                llm_latency_ms = GREATEST(analytics.message_lifecycle.llm_latency_ms, EXCLUDED.llm_latency_ms),
                generation_id = COALESCE(analytics.message_lifecycle.generation_id, EXCLUDED.generation_id),
                updated_at = now()
            """, conn, tx))
        {
            bind(cmd);
            stageRows = await cmd.ExecuteNonQueryAsync(ct);
        }
        // 2. Legacy rows: raw messages with no platform trace at all (no extraction, no archived event) and older than the grace — the pre-stage pipeline;
        // outcome from processing_status, timings and completion unknown. A platform raw the consumer has not projected yet is left to the events.
        long legacyRows;
        await using (var cmd = new NpgsqlCommand(
            $$"""
            INSERT INTO analytics.message_lifecycle (raw_message_id, run_id, source_id, source_message_key, source_revision, lane, published_at, received_at, stored_at,
                has_text, text_length, has_payload, is_edit, analyzed_at, analysis_outcome, method, fact_count, unlocated_facts, timings_available, expected_branches, branches_done,
                domain_completed_at, completion_available, incident_ids, track_ids, alert_ids, llm_calls, llm_input_tokens, llm_cache_tokens, llm_output_tokens, llm_cost_usd, llm_latency_ms, source_of_truth, updated_at)
            SELECT r.raw_message_id, @legacy, r.source_id, r.source_message_key, r.source_revision, 'legacy', r.published_at, r.received_at, NULL,
                   r.raw_text IS NOT NULL AND r.raw_text <> '', coalesce(length(r.raw_text), 0), r.raw_payload IS NOT NULL, r.source_revision <> '0',
                   r.processed_at,
                   CASE r.processing_status WHEN 1 THEN 'legacy' WHEN 2 THEN 'failed' WHEN 3 THEN 'unsupported' ELSE NULL END,
                   CASE WHEN r.processing_status = 1 THEN 'legacy' END,
                   (SELECT count(*)::int FROM targets t WHERE t.raw_message_id = r.raw_message_id),
                   0, false, '{}', '{}',
                   r.processed_at, false,
                   '{}',
                   coalesce((SELECT array_agg(DISTINCT tt.target_track_id) FROM targets t JOIN track_targets tt ON tt.target_id = t.target_id WHERE t.raw_message_id = r.raw_message_id), '{}'),
                   coalesce((SELECT array_agg(DISTINCT a.air_alert_id) FROM air_alerts a WHERE a.start_raw_message_id = r.raw_message_id OR a.end_raw_message_id = r.raw_message_id), '{}'),
                   coalesce(l.calls, 0), coalesce(l.input, 0), coalesce(l.cache, 0), coalesce(l.output, 0), coalesce(l.cost, 0), coalesce(l.latency, 0), 'backfill', now()
            FROM raw_messages r
            LEFT JOIN LATERAL (SELECT count(*)::int calls, sum(input_tokens) input, sum(coalesce(cache_read_input_tokens, 0) + coalesce(cache_creation_input_tokens, 0)) cache, sum(output_tokens) output, sum(estimated_cost_usd) cost, sum(duration_ms)::int latency
                               FROM llm_requests q WHERE q.raw_message_id = r.raw_message_id AND q.run_id IS NULL) l ON true
            WHERE {{filter}}
              AND r.received_at < now() - @grace
              AND NOT EXISTS (SELECT 1 FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id)
              AND NOT EXISTS (SELECT 1 FROM messaging.events e WHERE e.raw_message_id = r.raw_message_id)
              AND NOT EXISTS (SELECT 1 FROM analytics.message_lifecycle ml WHERE ml.raw_message_id = r.raw_message_id AND ml.source_of_truth <> 'backfill')
            ON CONFLICT (raw_message_id, run_id) DO UPDATE SET
                analysis_outcome = COALESCE(analytics.message_lifecycle.analysis_outcome, EXCLUDED.analysis_outcome),
                analyzed_at = COALESCE(analytics.message_lifecycle.analyzed_at, EXCLUDED.analyzed_at),
                fact_count = EXCLUDED.fact_count, track_ids = EXCLUDED.track_ids, alert_ids = EXCLUDED.alert_ids, updated_at = now()
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("legacy", LegacyRun);
            cmd.Parameters.AddWithValue("grace", grace);
            bind(cmd);
            legacyRows = await cmd.ExecuteNonQueryAsync(ct);
        }
        long kept;
        await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM analytics.message_lifecycle ml JOIN raw_messages r ON r.raw_message_id = ml.raw_message_id WHERE {filter} AND ml.source_of_truth = 'event'", conn, tx))
        {
            bind(cmd);
            kept = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return (stageRows + legacyRows, legacyRows, kept);
    }
}
