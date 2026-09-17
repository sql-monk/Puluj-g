using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Analytics.Contracts;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Lifecycle;

/// <summary>
/// The «Аналітика повідомлень» report (plan §10, ADR-0013, P15) over `analytics.message_lifecycle` with known denominators: raw
/// rows, posts and edits are counted from the roots, analyses and facts per (raw, run) of the active kinds (live/history/legacy —
/// a replay run's rows are a separate history, not mixed into the funnel), deliveries stay in the ops snapshot (P13). Storage is
/// UTC; buckets are hours for a day and Europe/Kyiv days for longer windows. Unknown is unknown: legacy timings and completion are
/// `unavailable` counts, precision/recall and rules-vs-LLM say so instead of a number. Read-only, statement timeout, short cache.
/// </summary>
public sealed class LifecycleReportService(IDbContextFactory<AnalyticsDbContext> factory, IOptions<AnalyticsOptions> options, TimeProvider clock)
{
    public static readonly int[] AllowedHours = [24, 168, 720];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (DateTimeOffset At, LifecycleReportDto Report)> _cache = [];

    public async Task<LifecycleReportDto?> ReportAsync(int hours, CancellationToken ct)
    {
        if (!AllowedHours.Contains(hours))
        {
            return null;
        }
        var now = clock.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.ReportCacheSeconds));
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(hours, out var cached) && now - cached.At < ttl)
            {
                return cached.Report;
            }
            var report = await ComputeAsync(hours, now, ct);
            _cache[hours] = (now, report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LifecycleStatusDto> StatusAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var available = (await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('analytics.message_lifecycle') IS NOT NULL AND to_regclass('analytics.state') IS NOT NULL AS \"Value\"").ToListAsync(ct)).First();
        if (!available)
        {
            return new LifecycleStatusDto(false, new LifecycleBackfillDto(0, 0, 0, 0, false, null), null);
        }
        var cursor = await LifecycleBackfill.CursorAsync(conn, ct);
        long max, rawRows, projectedRows;
        await using (var m = new NpgsqlCommand("SELECT coalesce(max(raw_message_id), 0), count(*) FROM raw_messages", conn))
        {
            await using var reader = await m.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            max = reader.GetInt64(0);
            rawRows = reader.GetInt64(1);
        }
        await using (var m = new NpgsqlCommand("SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle", conn))
        {
            projectedRows = (long)(await m.ExecuteScalarAsync(ct))!;
        }
        var at = await db.State.AsNoTracking().Where(s => s.Key == LifecycleBackfill.CursorKey).Select(s => (DateTimeOffset?)s.UpdatedAt).FirstOrDefaultAsync(ct);
        return new LifecycleStatusDto(true, new LifecycleBackfillDto(cursor, max, rawRows, projectedRows, cursor >= max, at), await LifecycleReconciliation.LastReportAsync(db, ct));
    }

    private async Task<LifecycleReportDto> ComputeAsync(int hours, DateTimeOffset now, CancellationToken ct)
    {
        var from = now - TimeSpan.FromHours(hours);
        var bucket = hours <= 24 ? "hour" : "day";
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);
        await Exec(conn, tx, "SET LOCAL statement_timeout = '20s'", ct);
        // The funnel and everything per raw use the live/history/legacy rows only; replay rows are history (see History).
        const string Scope = "ml.received_at >= @from AND ml.lane <> 'replay'";

        LifecycleFunnelDto funnel;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT count(DISTINCT ml.raw_message_id), count(DISTINCT (ml.source_id, ml.source_message_key)),
                    count(*) FILTER (WHERE ml.stored_at IS NOT NULL), count(*) FILTER (WHERE ml.analyzed_at IS NOT NULL), count(*) FILTER (WHERE ml.fact_count > 0),
                    count(*) FILTER (WHERE EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = ml.raw_message_id)),
                    count(*) FILTER (WHERE EXISTS (SELECT 1 FROM processing.observations o WHERE o.raw_message_id = ml.raw_message_id AND o.run_id = ml.run_id)),
                    count(*) FILTER (WHERE cardinality(ml.incident_ids) > 0), count(*) FILTER (WHERE cardinality(ml.track_ids) > 0), count(*) FILTER (WHERE cardinality(ml.alert_ids) > 0),
                   count(*) FILTER (WHERE ml.domain_completed_at IS NOT NULL),
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id WHERE g.is_active AND i.incident_id = ANY(ml.incident_ids))),
                   count(*) FILTER (WHERE ml.analyzed_at IS NULL AND ml.received_at < @stale AND ml.analysis_outcome IS NULL),
                   count(*) FILTER (WHERE ml.analyzed_at IS NOT NULL AND ml.domain_completed_at IS NULL AND ml.completion_available AND ml.analyzed_at < @stale),
                   count(*) FILTER (WHERE NOT ml.timings_available), count(*) FILTER (WHERE NOT ml.completion_available),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ml.analyzed_at - ml.stored_at)::float8) FILTER (WHERE ml.timings_available AND ml.stored_at IS NOT NULL AND ml.analyzed_at IS NOT NULL),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM ml.analyzed_at - ml.stored_at)::float8) FILTER (WHERE ml.timings_available AND ml.stored_at IS NOT NULL AND ml.analyzed_at IS NOT NULL),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ml.domain_completed_at - ml.analyzed_at)::float8) FILTER (WHERE ml.timings_available AND ml.analyzed_at IS NOT NULL AND ml.domain_completed_at IS NOT NULL),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM ml.domain_completed_at - ml.analyzed_at)::float8) FILTER (WHERE ml.timings_available AND ml.analyzed_at IS NOT NULL AND ml.domain_completed_at IS NOT NULL)
            FROM analytics.message_lifecycle ml WHERE {Scope}
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("stale", now - TimeSpan.FromMinutes(options.Value.LifecycleStaleMinutes));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            funnel = new LifecycleFunnelDto(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), Nullable(r, 16), Nullable(r, 17), Nullable(r, 18), Nullable(r, 19));
        }

        var timeline = new List<LifecycleBucketDto>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT b, count(DISTINCT ml.raw_message_id), count(*) FILTER (WHERE ml.analyzed_at IS NOT NULL), count(*) FILTER (WHERE ml.fact_count > 0),
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = ml.raw_message_id)),
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM processing.observations o WHERE o.raw_message_id = ml.raw_message_id AND o.run_id = ml.run_id)),
                   count(*) FILTER (WHERE cardinality(ml.incident_ids) > 0), count(*) FILTER (WHERE ml.domain_completed_at IS NOT NULL),
                   count(*) FILTER (WHERE ml.analysis_outcome = 'failed'), count(*) FILTER (WHERE NOT ml.has_text)
            FROM analytics.message_lifecycle ml
            CROSS JOIN LATERAL (SELECT CASE WHEN @bucket = 'hour' THEN date_trunc('hour', ml.received_at) ELSE (date_trunc('day', ml.received_at AT TIME ZONE 'Europe/Kyiv') AT TIME ZONE 'Europe/Kyiv') END AS b) x
            WHERE {Scope} GROUP BY b ORDER BY b
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("bucket", bucket);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                timeline.Add(new LifecycleBucketDto(r.GetFieldValue<DateTimeOffset>(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9)));
            }
        }

        var sources = new List<LifecycleSourceDto>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT ml.source_id, s.code, count(DISTINCT ml.raw_message_id), count(DISTINCT ml.source_message_key), count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.is_edit),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE NOT ml.has_text), count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.has_payload),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.analyzed_at IS NOT NULL), count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.fact_count > 0),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = ml.raw_message_id)),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE EXISTS (SELECT 1 FROM processing.observations o WHERE o.raw_message_id = ml.raw_message_id AND o.run_id = ml.run_id)),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE cardinality(ml.incident_ids) > 0), count(DISTINCT ml.raw_message_id) FILTER (WHERE cardinality(ml.track_ids) > 0), count(DISTINCT ml.raw_message_id) FILTER (WHERE cardinality(ml.alert_ids) > 0),
                   coalesce(sum(ml.fact_count), 0), percentile_cont(0.5) WITHIN GROUP (ORDER BY ml.text_length::float8) FILTER (WHERE ml.has_text),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ml.received_at - ml.published_at)::float8),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM ml.received_at - ml.published_at)::float8),
                   count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.lane = 'live'), count(DISTINCT ml.raw_message_id) FILTER (WHERE ml.lane = 'history'),
                   (SELECT max(gap)::float8 FROM (SELECT extract(epoch FROM published_at - lag(published_at) OVER (ORDER BY published_at)) AS gap FROM (SELECT DISTINCT published_at FROM analytics.message_lifecycle g WHERE g.source_id = ml.source_id AND g.received_at >= @from AND g.lane <> 'replay') p) gaps)
            FROM analytics.message_lifecycle ml JOIN sources s ON s.source_id = ml.source_id
            WHERE {Scope} GROUP BY ml.source_id, s.code ORDER BY 3 DESC
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                sources.Add(new LifecycleSourceDto(r.GetInt32(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), Nullable(r, 15), Nullable(r, 16), Nullable(r, 17), r.GetInt64(18), r.GetInt64(19), Nullable(r, 20)));
            }
        }

        var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);
        var methods = new Dictionary<string, long>(StringComparer.Ordinal);
        var ruleVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        var modelVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        long multiFact, unlocated, totalFacts;
        await using (var cmd = new NpgsqlCommand($"SELECT coalesce(ml.analysis_outcome, 'pending'), count(*) FROM analytics.message_lifecycle ml WHERE {Scope} GROUP BY 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { outcomes[r.GetString(0)] = r.GetInt64(1); }
        }
        await using (var cmd = new NpgsqlCommand($"SELECT coalesce(ml.method, 'pending'), count(*) FROM analytics.message_lifecycle ml WHERE {Scope} AND ml.analyzed_at IS NOT NULL GROUP BY 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { methods[r.GetString(0)] = r.GetInt64(1); }
        }
        await using (var cmd = new NpgsqlCommand($"SELECT count(*) FILTER (WHERE ml.fact_count > 1), coalesce(sum(ml.unlocated_facts), 0), coalesce(sum(ml.fact_count), 0) FROM analytics.message_lifecycle ml WHERE {Scope}", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            multiFact = r.GetInt64(0);
            unlocated = r.GetInt64(1);
            totalFacts = r.GetInt64(2);
        }
        await using (var cmd = new NpgsqlCommand($"SELECT coalesce(ml.versions->>'ruleset_id', ml.versions->>'rules', 'unknown'), count(*) FROM analytics.message_lifecycle ml WHERE {Scope} AND ml.versions IS NOT NULL GROUP BY 1 ORDER BY 2 DESC LIMIT 20", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { ruleVersions[r.GetString(0)] = r.GetInt64(1); }
        }
        await using (var cmd = new NpgsqlCommand($"SELECT ml.versions->>'model', count(*) FROM analytics.message_lifecycle ml WHERE {Scope} AND ml.versions->>'model' IS NOT NULL GROUP BY 1 ORDER BY 2 DESC LIMIT 20", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { modelVersions[r.GetString(0)] = r.GetInt64(1); }
        }
        var parse = new LifecycleParseDto(outcomes, methods, multiFact, unlocated, totalFacts, ruleVersions, modelVersions);

        // Quality: review outcomes are the operator's revisions of incidents in the window; precision/recall need a labelled sample (P16).
        var review = new Dictionary<string, long>(StringComparer.Ordinal);
        // System actors are `{role}@{instance}` (incident-worker@…, watchdog@…, replay@…); an operator is any other actor (free text, may contain '@').
        await using (var cmd = new NpgsqlCommand("SELECT change, count(*) FROM incident_revisions WHERE recorded_at >= @from AND actor NOT SIMILAR TO '(%-worker|watchdog|replay|projection|finalizer|parser|normalizer|system)@%' AND actor <> 'system' GROUP BY 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { review[r.GetString(0)] = r.GetInt64(1); }
        }
        var quality = new LifecycleQualityDto(review, null, "unavailable: потрібна розмічена вибірка (P16)", "unavailable: shadow-порівняння rules vs LLM (P16)");

        var byModel = new List<LifecycleCostModelDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT model, count(*), coalesce(sum(input_tokens), 0), coalesce(sum(coalesce(cache_read_input_tokens, 0) + coalesce(cache_creation_input_tokens, 0)), 0), coalesce(sum(output_tokens), 0), coalesce(sum(estimated_cost_usd), 0),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms::float8), percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms::float8),
                   count(*) FILTER (WHERE outcome IN ('429', 'api_error', 'error', 'invalid_response', 'timeout')), count(*) FILTER (WHERE outcome = 'late')
            FROM llm_requests WHERE occurred_at >= @from GROUP BY model ORDER BY 6 DESC
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                byModel.Add(new LifecycleCostModelDto(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetDecimal(5), Nullable(r, 6), Nullable(r, 7), r.GetInt64(8), r.GetInt64(9)));
            }
        }
        var bySource = new List<KeyValuePair<string, decimal>>();
        await using (var cmd = new NpgsqlCommand("SELECT s.code, coalesce(sum(q.estimated_cost_usd), 0) FROM llm_requests q JOIN sources s ON s.source_id = q.source_id WHERE q.occurred_at >= @from GROUP BY s.code ORDER BY 2 DESC LIMIT 20", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { bySource.Add(new KeyValuePair<string, decimal>(r.GetString(0), r.GetDecimal(1))); }
        }
        long llmRoots;
        await using (var cmd = new NpgsqlCommand($"SELECT count(DISTINCT ml.raw_message_id) FROM analytics.message_lifecycle ml WHERE {Scope} AND ml.llm_calls > 0", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            llmRoots = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var calls = byModel.Sum(m => m.Calls);
        var input = byModel.Sum(m => m.InputTokens);
        var cache = byModel.Sum(m => m.CacheTokens);
        var cost = new LifecycleCostDto(calls, llmRoots, byModel.Sum(m => m.CostUsd), input, cache, byModel.Sum(m => m.OutputTokens), input + cache > 0 ? (double)cache / (input + cache) : null, byModel, bySource);

        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand($"SELECT o.event_kind_code, count(*) FROM processing.observations o JOIN analytics.message_lifecycle ml ON ml.raw_message_id = o.raw_message_id AND ml.run_id = o.run_id WHERE {Scope} GROUP BY 1 ORDER BY 2 DESC LIMIT 40", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { kinds[r.GetString(0)] = r.GetInt64(1); }
        }
        var precision = new Dictionary<string, long>(StringComparer.Ordinal);
        long incidents, tracks, alerts, withProvenance, activeIncidents;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT CASE i.location_kind WHEN 6 THEN 'point' WHEN 4 THEN 'city' WHEN 3 THEN 'district' WHEN 5 THEN 'area' WHEN 2 THEN 'region' WHEN 1 THEN 'direction_only' ELSE 'unknown' END, count(*)
            FROM incidents i WHERE i.incident_id IN (SELECT unnest(ml.incident_ids) FROM analytics.message_lifecycle ml WHERE {Scope}) GROUP BY 1
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { precision[r.GetString(0)] = r.GetInt64(1); }
        }
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT (SELECT count(DISTINCT id) FROM analytics.message_lifecycle ml, unnest(ml.incident_ids) id WHERE {Scope}),
                   (SELECT count(DISTINCT id) FROM analytics.message_lifecycle ml, unnest(ml.track_ids) id WHERE {Scope}),
                   (SELECT count(DISTINCT id) FROM analytics.message_lifecycle ml, unnest(ml.alert_ids) id WHERE {Scope}),
                   (SELECT count(*) FROM incidents i WHERE i.canonical_observation_id IS NOT NULL AND i.incident_id IN (SELECT unnest(ml.incident_ids) FROM analytics.message_lifecycle ml WHERE {Scope})),
                   (SELECT count(*) FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id WHERE g.is_active AND i.incident_id IN (SELECT unnest(ml.incident_ids) FROM analytics.message_lifecycle ml WHERE {Scope}))
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            incidents = r.GetInt64(0);
            tracks = r.GetInt64(1);
            alerts = r.GetInt64(2);
            withProvenance = r.GetInt64(3);
            activeIncidents = r.GetInt64(4);
        }
        var results = new LifecycleResultsDto(kinds, precision, incidents, tracks, alerts, withProvenance, activeIncidents);

        var runs = new List<LifecycleRunDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT ml.run_id, coalesce(pr.kind, CASE WHEN ml.lane = 'legacy' THEN 'legacy' ELSE 'unknown' END), pr.versions->>'pipeline_version', coalesce(ml.analysis_outcome, 'pending'), count(*)
            FROM analytics.message_lifecycle ml LEFT JOIN processing.runs pr ON pr.run_id = ml.run_id
            WHERE ml.received_at >= @from GROUP BY 1, 2, 3, 4 ORDER BY 1
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var acc = new Dictionary<Guid, (string Kind, string? Version, Dictionary<string, long> Outcomes)>();
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                if (!acc.TryGetValue(id, out var run))
                {
                    acc[id] = run = (r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), new Dictionary<string, long>(StringComparer.Ordinal));
                }
                run.Outcomes[r.GetString(3)] = r.GetInt64(4);
            }
            runs.AddRange(acc.Select(kv => new LifecycleRunDto(kv.Key, kv.Value.Kind, kv.Value.Version, kv.Value.Outcomes.Values.Sum(), kv.Value.Outcomes)));
        }
        var byGeneration = new List<KeyValuePair<string, long>>();
        long activeGenerationIncidents = 0;
        await using (var cmd = new NpgsqlCommand("SELECT i.generation_id::text || CASE WHEN g.is_active THEN ' (active)' ELSE '' END, count(*), g.is_active FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id WHERE i.first_reported_at >= @from GROUP BY 1, 3 ORDER BY 2 DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("from", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                byGeneration.Add(new KeyValuePair<string, long>(r.GetString(0), r.GetInt64(1)));
                if (r.GetBoolean(2)) { activeGenerationIncidents += r.GetInt64(1); }
            }
        }
        var history = new LifecycleHistoryDto(runs, activeGenerationIncidents, byGeneration);
        await tx.RollbackAsync(ct);
        return new LifecycleReportDto(from, now, hours, bucket, funnel, timeline, sources, parse, quality, cost, results, history, await LifecycleReconciliation.LastReportAsync(db, ct));
    }

    private static double? Nullable(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
