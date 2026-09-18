using Microsoft.EntityFrameworkCore;
using Puluj.Contracts;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Admin;

/// <summary>
/// Buckets of the pipeline report, the same way the SQL groups them (`date_trunc(unit, ts, 'Europe/Kyiv')`): hours for a
/// day, calendar days (Kyiv) for a week or a month. Pure, so the alignment of the series is unit-tested.
/// </summary>
public static class PipelineBuckets
{
    public static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    public static readonly int[] AllowedHours = [24, 168, 720];

    public static string Unit(int hours) => hours <= 48 ? "hour" : "day";

    /// <summary>
    /// Start of the period and the start of every bucket up to <paramref name="now"/>: the last `hours / unit` buckets
    /// including the current, partial one. Hour buckets step in UTC (Kyiv is a whole-hour offset), day buckets on the
    /// Kyiv calendar, so a DST day is 23 or 25 hours long, as in Postgres.
    /// </summary>
    public static (DateTimeOffset From, string Unit, List<DateTimeOffset> Starts) Period(int hours, DateTimeOffset now)
    {
        var unit = Unit(hours);
        var starts = new List<DateTimeOffset>();
        if (unit == "hour")
        {
            var utc = now.UtcDateTime;
            var last = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            var from = last.AddHours(-(hours - 1));
            for (var cur = from; cur <= last; cur = cur.AddHours(1))
            {
                starts.Add(cur);
            }
            return (from, unit, starts);
        }
        var days = Math.Max(1, hours / 24);
        var today = TimeZoneInfo.ConvertTime(now, Kyiv).Date;
        for (var d = today.AddDays(-(days - 1)); d <= today; d = d.AddDays(1))
        {
            starts.Add(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), Kyiv), TimeSpan.Zero));
        }
        return (starts[0], unit, starts);
    }

    /// <summary>Index of the bucket an instant belongs to: the last start not after it (the first bucket for anything earlier).</summary>
    public static int Index(IReadOnlyList<DateTimeOffset> starts, DateTimeOffset at)
    {
        int lo = 0, hi = starts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= at)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }
}

/// <summary>
/// `GET /ops/pipeline`: what the pipeline received, produced and how long it took, for the last day / week / month
/// (docs/plan-admin-ops.md §2.4). Everything is aggregated in SQL (`GROUP BY`, `ROLLUP` for the totals, `percentile_cont`
/// over raw_messages.processing_ms) — one query per group, never a loop over the sources.
/// </summary>
public static class PipelineReport
{
    private const string Tz = "Europe/Kyiv";
    private const int RecentErrors = 50;

    // Unmapped query types follow the snake_case naming convention: the SQL columns are aliased to match.
    // `GROUP BY ROLLUP (source_id)` adds one row with source_id NULL — the total over every source.
    private sealed record SourceRow(int? SourceId, long Received, long Processed, long Skipped, long Failed, long Pending, long InProgress,
        long WithTargets, double? P50Ms, double? P90Ms, double? MeanMs, double? MedianLagSeconds);
    private sealed record TargetsRow(int? SourceId, long N, long Duplicates);
    private sealed record TracksRow(int? SourceId, long N);
    private sealed record SourceBucketRow(int SourceId, DateTimeOffset BucketAt, long N);
    private sealed record ProcessedBucketRow(DateTimeOffset BucketAt, long N, double? P50Ms, double? P90Ms);
    private sealed record BucketRow(DateTimeOffset BucketAt, long N);
    private sealed record ErrorBucketRow(DateTimeOffset BucketAt, long N, long Transient);
    private sealed record InstanceRow(string Instance, long Processed, double? P50Ms, double? P90Ms, DateTimeOffset? LastAt);
    private sealed record StatusCount(int Status, long Count);
    private sealed record StageCount(string Stage, long Count);

    public static async Task<PipelineReportDto> BuildAsync(PulujDbContext db, int hours, DateTimeOffset now, CancellationToken ct)
    {
        var (from, unit, starts) = PipelineBuckets.Period(hours, now);
        var to = now;

        // Messages: received by received_at, outcomes by processed_at, current statuses; timings only exist for successes.
        var sourceRows = await db.Database.SqlQuery<SourceRow>($"""
            SELECT r.source_id AS source_id,
                   count(*) FILTER (WHERE r.received_at >= {from} AND r.received_at < {to}) AS received,
                   count(*) FILTER (WHERE r.processing_status = 1 AND r.processed_at >= {from} AND r.processed_at < {to}) AS processed,
                   count(*) FILTER (WHERE r.processing_status = 3 AND r.processed_at >= {from} AND r.processed_at < {to}) AS skipped,
                   count(*) FILTER (WHERE r.processing_status = 2 AND r.processed_at >= {from} AND r.processed_at < {to}) AS failed,
                   count(*) FILTER (WHERE r.processing_status = 0) AS pending,
                   count(*) FILTER (WHERE r.processing_status = 4) AS in_progress,
                   count(*) FILTER (WHERE r.processing_status = 1 AND r.processed_at >= {from} AND r.processed_at < {to}
                                      AND EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = r.raw_message_id)) AS with_targets,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY CAST(r.processing_ms AS float8))
                       FILTER (WHERE r.processing_ms IS NOT NULL AND r.processed_at >= {from} AND r.processed_at < {to}) AS p50ms,
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY CAST(r.processing_ms AS float8))
                       FILTER (WHERE r.processing_ms IS NOT NULL AND r.processed_at >= {from} AND r.processed_at < {to}) AS p90ms,
                   CAST(avg(r.processing_ms) FILTER (WHERE r.processed_at >= {from} AND r.processed_at < {to}) AS float8) AS mean_ms,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY CAST(extract(epoch FROM r.received_at - r.published_at) AS float8))
                       FILTER (WHERE r.received_at >= {from} AND r.received_at < {to}
                                 AND r.received_at >= r.published_at AND r.received_at - r.published_at < interval '6 hours') AS median_lag_seconds
            FROM raw_messages r
            WHERE r.received_at >= {from} OR r.processed_at >= {from} OR r.processing_status IN (0, 4)
            GROUP BY ROLLUP (r.source_id)
            """).ToListAsync(ct);
        // Everything the pipeline produced in the period, duplicates included (they are counted, not excluded).
        var targetRows = await db.Database.SqlQuery<TargetsRow>($"""
            SELECT source_id AS source_id, count(*) AS n, count(*) FILTER (WHERE duplicate_of_target_id IS NOT NULL) AS duplicates
            FROM targets
            WHERE observed_at >= {from} AND observed_at < {to}
            GROUP BY ROLLUP (source_id)
            """).ToListAsync(ct);
        // A track belongs to the source of its first target (target_tracks has no source_id).
        var trackRows = await db.Database.SqlQuery<TracksRow>($"""
            SELECT t.source_id AS source_id, count(*) AS n
            FROM target_tracks tt
            JOIN track_targets x ON x.target_track_id = tt.target_track_id AND x.sequence = 1
            JOIN targets t ON t.target_id = x.target_id
            WHERE tt.first_seen_at >= {from} AND tt.first_seen_at < {to}
            GROUP BY ROLLUP (t.source_id)
            """).ToListAsync(ct);
        var receivedBuckets = await db.Database.SqlQuery<SourceBucketRow>($"""
            SELECT source_id AS source_id, date_trunc({unit}, received_at, {Tz}) AS bucket_at, count(*) AS n
            FROM raw_messages
            WHERE received_at >= {from} AND received_at < {to}
            GROUP BY 1, 2
            """).ToListAsync(ct);
        var processedBuckets = await db.Database.SqlQuery<ProcessedBucketRow>($"""
            SELECT date_trunc({unit}, processed_at, {Tz}) AS bucket_at, count(*) AS n,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY CAST(processing_ms AS float8)) FILTER (WHERE processing_ms IS NOT NULL) AS p50ms,
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY CAST(processing_ms AS float8)) FILTER (WHERE processing_ms IS NOT NULL) AS p90ms
            FROM raw_messages
            WHERE processing_status = 1 AND processed_at >= {from} AND processed_at < {to}
            GROUP BY 1
            """).ToListAsync(ct);
        var targetBuckets = await db.Database.SqlQuery<BucketRow>($"""
            SELECT date_trunc({unit}, observed_at, {Tz}) AS bucket_at, count(*) AS n
            FROM targets
            WHERE observed_at >= {from} AND observed_at < {to}
            GROUP BY 1
            """).ToListAsync(ct);
        var errorBuckets = await db.Database.SqlQuery<ErrorBucketRow>($"""
            SELECT date_trunc({unit}, occurred_at, {Tz}) AS bucket_at, count(*) AS n, count(*) FILTER (WHERE stage = 'transient') AS transient
            FROM processing_errors
            WHERE occurred_at >= {from} AND occurred_at < {to}
            GROUP BY 1
            """).ToListAsync(ct);
        var instanceRows = await db.Database.SqlQuery<InstanceRow>($"""
            SELECT claimed_by AS instance, count(*) AS processed,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY CAST(processing_ms AS float8)) FILTER (WHERE processing_ms IS NOT NULL) AS p50ms,
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY CAST(processing_ms AS float8)) FILTER (WHERE processing_ms IS NOT NULL) AS p90ms,
                   max(processed_at) AS last_at
            FROM raw_messages
            WHERE claimed_by IS NOT NULL AND processing_status = 1 AND processed_at >= {from} AND processed_at < {to}
            GROUP BY 1
            """).ToListAsync(ct);
        var processingStatuses = await db.Database.SqlQueryRaw<StatusCount>("SELECT processing_status AS status, count(*) AS count FROM raw_messages GROUP BY 1").ToListAsync(ct);
        var stages = await db.Database.SqlQuery<StageCount>($"SELECT stage AS stage, count(*) AS count FROM processing_errors WHERE occurred_at >= {from} AND occurred_at < {to} GROUP BY 1").ToListAsync(ct);
        var recent = await db.ProcessingErrors.AsNoTracking().OrderByDescending(e => e.OccurredAt).Take(RecentErrors)
            .Select(e => new ProcessingErrorDto(e.ProcessingErrorId, e.OccurredAt, e.Stage, e.Message, e.SourceId, e.RawMessageId, e.Exception))
            .ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().OrderByDescending(s => s.Enabled).ThenByDescending(s => s.Priority).ThenBy(s => s.Name).ToListAsync(ct);

        // Fold: the rollup row is the total, the rest go by source.
        var totalRow = sourceRows.FirstOrDefault(r => r.SourceId is null);
        var bySource = sourceRows.Where(r => r.SourceId is not null).ToDictionary(r => r.SourceId!.Value);
        var targetsTotal = targetRows.FirstOrDefault(r => r.SourceId is null);
        var targetsBySource = targetRows.Where(r => r.SourceId is not null).ToDictionary(r => r.SourceId!.Value, r => r.N);
        var tracksTotal = trackRows.FirstOrDefault(r => r.SourceId is null);
        var tracksBySource = trackRows.Where(r => r.SourceId is not null).ToDictionary(r => r.SourceId!.Value, r => r.N);
        var errorsTotal = stages.Sum(s => s.Count);

        var totals = new PipelineTotalsDto(
            totalRow?.Received ?? 0, totalRow?.Processed ?? 0, totalRow?.Skipped ?? 0, totalRow?.Failed ?? 0, totalRow?.Pending ?? 0, totalRow?.InProgress ?? 0,
            targetsTotal?.N ?? 0, targetsTotal?.Duplicates ?? 0, tracksTotal?.N ?? 0, errorsTotal,
            Round(totalRow?.P50Ms), Round(totalRow?.P90Ms), Round(totalRow?.MeanMs));

        var series = new Dictionary<int, int[]>();
        var receivedPerBucket = new int[starts.Count];
        foreach (var r in receivedBuckets)
        {
            var i = PipelineBuckets.Index(starts, r.BucketAt);
            if (!series.TryGetValue(r.SourceId, out var s))
            {
                series[r.SourceId] = s = new int[starts.Count];
            }
            s[i] += (int)r.N;
            receivedPerBucket[i] += (int)r.N;
        }
        var sourceDtos = sources.Select(s =>
            {
                var r = bySource.GetValueOrDefault(s.SourceId);
                return new PipelineSourceDto(s.SourceId, s.Code, s.Name, s.Type.ToString(), s.Enabled,
                    r?.Received ?? 0, r?.Processed ?? 0, r?.Skipped ?? 0, r?.Failed ?? 0, r?.Pending ?? 0,
                    r?.WithTargets ?? 0, targetsBySource.GetValueOrDefault(s.SourceId), tracksBySource.GetValueOrDefault(s.SourceId),
                    Round(r?.MedianLagSeconds), Round(r?.P50Ms), Round(r?.P90Ms),
                    series.GetValueOrDefault(s.SourceId) ?? new int[starts.Count]);
            })
            .Where(s => s.Enabled || s.Received > 0 || s.Processed > 0 || s.Pending > 0)
            .OrderByDescending(s => s.Received).ThenBy(s => s.Name)
            .ToList();

        var processed = new (int N, double? P50, double? P90)[starts.Count];
        foreach (var r in processedBuckets)
        {
            var i = PipelineBuckets.Index(starts, r.BucketAt);
            processed[i] = (processed[i].N + (int)r.N, r.P50Ms, r.P90Ms);
        }
        var targetsPerBucket = new int[starts.Count];
        foreach (var r in targetBuckets)
        {
            targetsPerBucket[PipelineBuckets.Index(starts, r.BucketAt)] += (int)r.N;
        }
        var errors = new (int N, int Transient)[starts.Count];
        foreach (var r in errorBuckets)
        {
            var i = PipelineBuckets.Index(starts, r.BucketAt);
            errors[i] = (errors[i].N + (int)r.N, errors[i].Transient + (int)r.Transient);
        }
        var timeline = starts.Select((at, i) => new PipelineBucketDto(at, receivedPerBucket[i], processed[i].N, targetsPerBucket[i], errors[i].N, errors[i].Transient,
            Round(processed[i].P50), Round(processed[i].P90))).ToList();

        var instances = instanceRows.OrderByDescending(r => r.Processed)
            .Select(r => new PipelineInstanceDto(r.Instance, r.Processed, Round(r.P50Ms), Round(r.P90Ms), r.LastAt)).ToList();

        return new PipelineReportDto(from, to, unit, starts, totals, sourceDtos, timeline, instances,
            processingStatuses.ToDictionary(q => ((ProcessingStatus)q.Status).ToString(), q => q.Count),
            stages.OrderByDescending(s => s.Count).ToDictionary(s => s.Stage, s => s.Count),
            recent);
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
}
