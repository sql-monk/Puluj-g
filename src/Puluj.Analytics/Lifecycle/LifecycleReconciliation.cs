using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Contracts;

namespace Puluj.Analytics.Lifecycle;

/// <summary>
/// Keeps the lifecycle projection honest (plan §10 «потрібні події їх появи та reconciliation», ADR-0013): (1) counts — raw rows,
/// posts and edits in the window versus what the projection holds (missing roots are backfilled from evidence right away); (2) late results —
/// rows still without an analysis although an extraction exists, and rows without a domain completion although every expected
/// branch has a terminal receipt (a `noop` branch emits no event; a lost event leaves a hole). «Late» = written by this sweep, not
/// by an event; the report counts them. Runs every Analytics loop pass (`Analytics:Interval`) over rows older than a short grace.
/// </summary>
public sealed class LifecycleReconciliation(IDbContextFactory<AnalyticsDbContext> factory, ILogger<LifecycleReconciliation> logger)
{
    public const string ReportKey = "lifecycle_reconciliation_report";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LifecycleReconciliationDto> RunAsync(TimeSpan window, TimeSpan grace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var since = DateTimeOffset.UtcNow - window;
        var cutoff = DateTimeOffset.UtcNow - grace;

        // 1. Late analyses: an extraction exists, the row never saw its message.analysis.completed.
        long lateAnalyses;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE analytics.message_lifecycle ml SET
                analyzed_at = x.created_at, analysis_outcome = x.outcome, method = x.method,
                fact_count = (SELECT count(*)::int FROM processing.observations o WHERE o.extraction_id = x.extraction_id),
                versions = COALESCE(ml.versions, x.versions), error = COALESCE(ml.error, left(x.error::text, 2000)),
                timings = COALESCE(ml.timings, (SELECT jsonb_object_agg(sr.stage || '_at', sr.finished_at) FROM processing.stage_results sr WHERE sr.raw_message_id = ml.raw_message_id AND sr.run_id = ml.run_id AND sr.finished_at IS NOT NULL)),
                timings_available = ml.timings_available OR EXISTS (SELECT 1 FROM processing.stage_results sr WHERE sr.raw_message_id = ml.raw_message_id AND sr.run_id = ml.run_id),
                source_of_truth = 'reconciliation', updated_at = now()
            FROM processing.extractions x
            WHERE x.raw_message_id = ml.raw_message_id AND x.run_id = ml.run_id AND ml.analyzed_at IS NULL AND ml.received_at >= @since AND ml.received_at < @cutoff
            """, conn))
        {
            cmd.Parameters.AddWithValue("since", since);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            lateAnalyses = await cmd.ExecuteNonQueryAsync(ct);
        }
        // 2. Late domain completions: every expected branch has a terminal receipt (completed | noop | waived), no event said so.
        long lateCompletions;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE analytics.message_lifecycle ml SET
                domain_completed_at = COALESCE(t.completed_at, ml.analyzed_at),
                incident_ids = CASE WHEN cardinality(ml.incident_ids) = 0 THEN coalesce((SELECT array_agg(DISTINCT io.incident_id) FROM incident_observations io JOIN processing.observations o ON o.observation_id = io.observation_id WHERE o.raw_message_id = ml.raw_message_id AND o.run_id = ml.run_id), '{}') ELSE ml.incident_ids END,
                track_ids = CASE WHEN cardinality(ml.track_ids) = 0 THEN coalesce((SELECT array_agg(DISTINCT tt.target_track_id) FROM targets tg JOIN track_targets tt ON tt.target_id = tg.target_id WHERE tg.raw_message_id = ml.raw_message_id), '{}') ELSE ml.track_ids END,
                alert_ids = CASE WHEN cardinality(ml.alert_ids) = 0 THEN coalesce((SELECT array_agg(DISTINCT a.air_alert_id) FROM air_alerts a WHERE a.start_raw_message_id = ml.raw_message_id OR a.end_raw_message_id = ml.raw_message_id), '{}') ELSE ml.alert_ids END,
                source_of_truth = 'reconciliation', updated_at = now()
            FROM (
                SELECT ml2.raw_message_id, ml2.run_id, max(d.completed_at) AS completed_at
                FROM analytics.message_lifecycle ml2
                LEFT JOIN messaging.events e ON e.raw_message_id = ml2.raw_message_id AND e.processing_run_id = ml2.run_id AND e.event_type = 'observations.recorded'
                LEFT JOIN processing.deliveries d ON d.event_id = e.event_id AND d.subscription_id = ANY(ml2.expected_branches)
                WHERE ml2.analyzed_at IS NOT NULL AND ml2.domain_completed_at IS NULL AND ml2.completion_available AND ml2.received_at >= @since AND ml2.received_at < @cutoff
                GROUP BY ml2.raw_message_id, ml2.run_id, ml2.expected_branches
                HAVING count(*) FILTER (WHERE d.event_id IS NOT NULL AND d.outcome IS NULL) = 0 AND count(d.event_id) >= cardinality(ml2.expected_branches)
            ) t
            WHERE t.raw_message_id = ml.raw_message_id AND t.run_id = ml.run_id
            """, conn))
        {
            cmd.Parameters.AddWithValue("since", since);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            lateCompletions = await cmd.ExecuteNonQueryAsync(ct);
        }
        // 3. Counts with known denominators: raw rows / posts / edits in the window (raw_messages) versus the projection's roots.
        long rawRows, posts, edits, projectedRaw, projectedPosts, missing, unavailableTimings, unavailableCompletion, pendingAnalysis, pendingDomain;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM raw_messages WHERE received_at >= @since),
                   (SELECT count(DISTINCT (source_id, source_message_key)) FROM raw_messages WHERE received_at >= @since),
                   (SELECT count(*) FROM raw_messages WHERE received_at >= @since AND source_revision <> '0'),
                   (SELECT count(DISTINCT raw_message_id) FROM analytics.message_lifecycle WHERE received_at >= @since),
                   (SELECT count(DISTINCT (source_id, source_message_key)) FROM analytics.message_lifecycle WHERE received_at >= @since),
                   (SELECT count(*) FROM raw_messages r WHERE r.received_at >= @since AND r.received_at < @cutoff AND NOT EXISTS (SELECT 1 FROM analytics.message_lifecycle ml WHERE ml.raw_message_id = r.raw_message_id)),
                   (SELECT count(*) FROM analytics.message_lifecycle WHERE received_at >= @since AND NOT timings_available),
                   (SELECT count(*) FROM analytics.message_lifecycle WHERE received_at >= @since AND NOT completion_available),
                   (SELECT count(*) FROM analytics.message_lifecycle WHERE received_at >= @since AND received_at < @cutoff AND analyzed_at IS NULL),
                   (SELECT count(*) FROM analytics.message_lifecycle WHERE received_at >= @since AND received_at < @cutoff AND analyzed_at IS NOT NULL AND domain_completed_at IS NULL AND completion_available)
            """, conn))
        {
            cmd.Parameters.AddWithValue("since", since);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            rawRows = reader.GetInt64(0);
            posts = reader.GetInt64(1);
            edits = reader.GetInt64(2);
            projectedRaw = reader.GetInt64(3);
            projectedPosts = reader.GetInt64(4);
            missing = reader.GetInt64(5);
            unavailableTimings = reader.GetInt64(6);
            unavailableCompletion = reader.GetInt64(7);
            pendingAnalysis = reader.GetInt64(8);
            pendingDomain = reader.GetInt64(9);
        }
        // 4. Missing roots (a raw.stored the projection never saw, older than the grace): filled from evidence by id — a bounded set, never a rewrite of the window.
        if (missing > 0)
        {
            var ids = new List<long>();
            await using (var find = new NpgsqlCommand("SELECT r.raw_message_id FROM raw_messages r WHERE r.received_at >= @since AND r.received_at < @cutoff AND NOT EXISTS (SELECT 1 FROM analytics.message_lifecycle ml WHERE ml.raw_message_id = r.raw_message_id) ORDER BY r.raw_message_id LIMIT 2000", conn))
            {
                find.Parameters.AddWithValue("since", since);
                find.Parameters.AddWithValue("cutoff", cutoff);
                await using var reader = await find.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ids.Add(reader.GetInt64(0));
                }
            }
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LifecycleBackfill.BackfillIdsAsync(conn, tx, ids, grace, ct);
            await tx.CommitAsync(ct);
        }
        var report = new LifecycleReconciliationDto(DateTimeOffset.UtcNow, (int)window.TotalHours, rawRows, posts, edits, projectedRaw, projectedPosts, missing, lateAnalyses, lateCompletions, pendingAnalysis, pendingDomain, unavailableTimings, unavailableCompletion);
        await using (var save = new NpgsqlCommand("INSERT INTO analytics.state (key, value, updated_at) VALUES (@k, @v, now()) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()", conn))
        {
            save.Parameters.AddWithValue("k", ReportKey);
            save.Parameters.AddWithValue("v", JsonSerializer.Serialize(report, Json));
            await save.ExecuteNonQueryAsync(ct);
        }
        if (missing > 0 || lateAnalyses > 0 || lateCompletions > 0)
        {
            logger.LogInformation("Lifecycle reconciliation ({Hours} h): raw {Raw} vs projected {Projected} (missing {Missing}); late analyses {LateA}, late completions {LateC}", (int)window.TotalHours, rawRows, projectedRaw, missing, lateAnalyses, lateCompletions);
        }
        return report;
    }

    public static async Task<LifecycleReconciliationDto?> LastReportAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var value = await db.State.AsNoTracking().Where(s => s.Key == ReportKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return value is null ? null : JsonSerializer.Deserialize<LifecycleReconciliationDto>(value, Json);
    }
}
