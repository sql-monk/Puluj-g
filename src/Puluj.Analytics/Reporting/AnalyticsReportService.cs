using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Contracts;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Reporting;

/// <summary>
/// Read side of the analytics schema for the admin panel and the service's own endpoints. Aggregates are computed on
/// request from `messages` / `copies` (no counters that could drift from the facts); days and hours are Kyiv time.
/// Works with the read-write admin role as well as the owner.
/// </summary>
public sealed class AnalyticsReportService(IDbContextFactory<AnalyticsDbContext> factory, IOptions<AnalyticsOptions> options, TimeProvider clock)
{
    private static readonly TimeZoneInfo Kyiv = TrackFirstsBuilder.Kyiv;

    public async Task<AnalyticsStatusDto> StatusAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var latest = await RawMessageReader.MaxIdAsync(db, ct);
        var heartbeat = await HeartbeatAsync(db, ct);
        var instance = await InstanceAsync(db, ct);
        if (!await InitializedAsync(db, ct))
        {
            return new AnalyticsStatusDto(false, 0, latest, latest, heartbeat, null, [], 0, 0, 0, 0, [], instance);
        }
        var watermark = await AnalysisRunner.WatermarkAsync(db, ct);
        var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct);
        var counts = (await db.Database.SqlQueryRaw<CountRow>("""
            SELECT (SELECT count(*) FROM analytics.messages) AS messages,
                   (SELECT count(*) FROM analytics.messages WHERE min_hash IS NOT NULL) AS fingerprinted,
                   (SELECT count(*) FROM analytics.copies) AS pairs,
                   (SELECT coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'analytics' AND c.relkind = 'r') AS schema_bytes
            """).ToListAsync(ct)).First();
        var migrations = await db.Database.SqlQueryRaw<string>("SELECT migration_id AS \"Value\" FROM analytics.\"__EFMigrationsHistory\" ORDER BY 1").ToListAsync(ct);
        var dtos = runs.Select(ToDto).ToList();
        return new AnalyticsStatusDto(true, watermark, latest, Math.Max(0, latest - watermark), heartbeat, dtos.FirstOrDefault(), dtos,
            counts.Messages, counts.Fingerprinted, counts.Pairs, counts.SchemaBytes, migrations, instance);
    }

    public async Task<AnalyticsReportDto?> ReportAsync(int days, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await InitializedAsync(db, ct))
        {
            return null;
        }
        days = Math.Clamp(days, 1, 90);
        var (sinceDay, since) = Period(days);
        var dayList = Enumerable.Range(0, days).Select(i => sinceDay.AddDays(i)).ToList();

        var sources = await db.Database.SqlQueryRaw<SourceRow>("SELECT source_id, code, name, enabled FROM sources ORDER BY priority DESC, source_id").ToListAsync(ct);
        var postDays = await db.Database.SqlQuery<PostDayRow>($"""
            SELECT source_id, (published_at AT TIME ZONE 'Europe/Kyiv')::date AS day, count(DISTINCT post_key)::int AS posts, count(*)::int AS row_count
            FROM analytics.messages WHERE published_at >= {since} GROUP BY 1, 2
            """).ToListAsync(ct);
        var hours = await db.Database.SqlQuery<HourRow>($"""
            SELECT source_id, extract(hour FROM published_at AT TIME ZONE 'Europe/Kyiv')::int AS hour, count(DISTINCT post_key)::int AS posts
            FROM analytics.messages WHERE published_at >= {since} GROUP BY 1, 2
            """).ToListAsync(ct);
        var copierDays = await db.Database.SqlQuery<CopierDayRow>($"""
            SELECT copy_source_id AS source_id, (copy_published_at AT TIME ZONE 'Europe/Kyiv')::date AS day, count(*)::int AS copies,
                   avg(delay_seconds) AS avg_delay, percentile_cont(0.5) WITHIN GROUP (ORDER BY delay_seconds) AS median_delay,
                   count(*) FILTER (WHERE kind >= {(int)CopyKind.Verbatim})::int AS verbatim
            FROM analytics.copies WHERE is_primary AND copy_published_at >= {since} GROUP BY 1, 2
            """).ToListAsync(ct);
        var originalDays = await db.Database.SqlQuery<OriginalDayRow>($"""
            SELECT original_source_id AS source_id, (copy_published_at AT TIME ZONE 'Europe/Kyiv')::date AS day, count(*)::int AS copied_by, avg(delay_seconds) AS avg_lead
            FROM analytics.copies WHERE is_primary AND copy_published_at >= {since} GROUP BY 1, 2
            """).ToListAsync(ct);
        var forwards = await db.Database.SqlQuery<ForwardRow>($"""
            SELECT source_id, count(DISTINCT post_key) FILTER (WHERE forwarded_source_id IS NOT NULL)::int AS internal,
                   count(DISTINCT post_key) FILTER (WHERE forwarded_external)::int AS external
            FROM analytics.messages WHERE published_at >= {since} GROUP BY 1
            """).ToListAsync(ct);
        var pairs = await db.Database.SqlQuery<PairRow>($"""
            SELECT copy_source_id AS copier_id, original_source_id AS original_id,
                   count(*) FILTER (WHERE is_primary)::int AS pair_count, count(*)::int AS count_all,
                   count(*) FILTER (WHERE is_primary AND kind = {(int)CopyKind.Verbatim})::int AS verbatim,
                   count(*) FILTER (WHERE is_primary AND kind = {(int)CopyKind.Forward})::int AS forwards,
                   coalesce(avg(delay_seconds) FILTER (WHERE is_primary), 0) AS avg_delay,
                   coalesce(percentile_cont(0.5) WITHIN GROUP (ORDER BY delay_seconds) FILTER (WHERE is_primary), 0) AS median_delay,
                   coalesce(min(delay_seconds) FILTER (WHERE is_primary), 0) AS min_delay,
                   coalesce(avg(jaccard) FILTER (WHERE is_primary), 0)::float AS avg_jaccard
            FROM analytics.copies WHERE copy_published_at >= {since} GROUP BY 1, 2 ORDER BY 3 DESC, 4 DESC
            """).ToListAsync(ct);
        var external = await db.Database.SqlQuery<ExternalRow>($"""
            SELECT source_id, forwarded_from AS channel_ref, count(DISTINCT post_key)::int AS forwards
            FROM analytics.messages WHERE forwarded_external AND forwarded_from IS NOT NULL AND published_at >= {since}
            GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 40
            """).ToListAsync(ct);
        var firsts = await db.Database.SqlQuery<FirstRow>($"""
            SELECT source_id, category_code, sum(firsts)::int AS firsts, sum(participations)::int AS participations,
                   sum(lag_seconds_sum) AS lag_sum, sum(lag_count)::int AS lag_count
            FROM analytics.track_firsts WHERE day >= {sinceDay} GROUP BY 1, 2
            """).ToListAsync(ct);

        var postsBy = postDays.ToLookup(r => r.SourceId);
        var hoursBy = hours.ToLookup(r => r.SourceId);
        var copierBy = copierDays.ToLookup(r => r.SourceId);
        var originalBy = originalDays.ToLookup(r => r.SourceId);
        var forwardBy = forwards.ToDictionary(r => r.SourceId);
        var sourceDtos = sources.Select(s =>
        {
            var mine = postsBy[s.SourceId].ToDictionary(r => r.Day);
            var copied = copierBy[s.SourceId].ToDictionary(r => r.Day);
            var lead = originalBy[s.SourceId].ToDictionary(r => r.Day);
            var posts = mine.Values.Sum(r => r.Posts);
            var rows = mine.Values.Sum(r => r.RowCount);
            var copies = copied.Values.Sum(r => r.Copies);
            var copiedBy = lead.Values.Sum(r => r.CopiedBy);
            var verbatim = copied.Values.Sum(r => r.Verbatim);
            var perHour = new int[24];
            foreach (var h in hoursBy[s.SourceId])
            {
                perHour[h.Hour] = h.Posts;
            }
            var perDay = dayList.Select(d => new SourceDayDto(d,
                mine.TryGetValue(d, out var p) ? p.Posts : 0,
                copied.TryGetValue(d, out var c) ? c.Copies : 0,
                lead.TryGetValue(d, out var l) ? l.CopiedBy : 0)).ToList();
            forwardBy.TryGetValue(s.SourceId, out var fw);
            // The median of the period is approximated by the copy-weighted mean of the daily medians.
            var medians = copied.Values.Where(r => r.Copies > 0).ToList();
            return new SourceAnalyticsDto(s.SourceId, s.Code, s.Name, s.Enabled, posts, rows - posts, copies, copiedBy,
                posts == 0 ? null : Math.Round(1 - (double)copies / posts, 3),
                copies == 0 ? null : Math.Round(copied.Values.Sum(r => r.AvgDelay * r.Copies) / copies, 1),
                medians.Count == 0 ? null : Math.Round(medians.Sum(r => r.MedianDelay * r.Copies) / medians.Sum(r => r.Copies), 1),
                copiedBy == 0 ? null : Math.Round(lead.Values.Sum(r => r.AvgLead * r.CopiedBy) / copiedBy, 1),
                copies == 0 ? null : Math.Round((double)verbatim / copies, 3),
                fw?.Internal ?? 0, fw?.External ?? 0, perHour, perDay);
        }).ToList();

        return new AnalyticsReportDto(dayList, sourceDtos,
            pairs.Select(p => new CopyPairDto(p.CopierId, p.OriginalId, p.PairCount, p.CountAll, p.Verbatim, p.Forwards,
                Math.Round(p.AvgDelay, 1), Math.Round(p.MedianDelay, 1), Math.Round(p.MinDelay, 1), Math.Round(p.AvgJaccard, 3))).ToList(),
            external.Select(e => new ExternalForwardDto(e.SourceId, e.ChannelRef, e.Forwards)).ToList(),
            firsts.Select(f => new TrackFirstDto(f.SourceId, f.CategoryCode, f.Firsts, f.Participations, f.LagCount == 0 ? null : Math.Round(f.LagSum / f.LagCount, 1))).ToList());
    }

    public async Task<IReadOnlyList<RecentCopyDto>> RecentAsync(int limit, int? sourceId, CopyKind? kind, bool primaryOnly, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await InitializedAsync(db, ct))
        {
            return [];
        }
        limit = Math.Clamp(limit, 1, 200);
        // SqlQueryRaw turns the {n} placeholders into parameters; the WHERE is assembled from the filters that are set.
        var where = new List<string>();
        var args = new List<object>();
        if (sourceId is { } sid)
        {
            where.Add($"(c.copy_source_id = {{{args.Count}}} OR c.original_source_id = {{{args.Count}}})");
            args.Add(sid);
        }
        if (kind is { } k)
        {
            where.Add($"c.kind = {{{args.Count}}}");
            args.Add((int)k);
        }
        if (primaryOnly)
        {
            where.Add("c.is_primary");
        }
        var sql = RecentSelect + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + $" ORDER BY c.found_at DESC, c.copy_published_at DESC LIMIT {{{args.Count}}}";
        args.Add(limit);
        var rows = await db.Database.SqlQueryRaw<RecentRow>(sql, args.ToArray()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>One copier → original pair over the period: aggregates, the delay histogram and the 20 latest examples (both texts). Null when the schema does not exist yet.</summary>
    public async Task<PairDetailsDto?> PairAsync(int copierId, int originalId, int days, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await InitializedAsync(db, ct))
        {
            return null;
        }
        days = Math.Clamp(days, 1, 90);
        var (_, since) = Period(days);
        var rows = await db.Database.SqlQuery<PairCopyRow>($"""
            SELECT delay_seconds, kind, is_primary, jaccard::float AS jaccard
            FROM analytics.copies WHERE copy_source_id = {copierId} AND original_source_id = {originalId} AND copy_published_at >= {since}
            """).ToListAsync(ct);
        var recent = await db.Database.SqlQueryRaw<RecentRow>(RecentSelect + " WHERE c.copy_source_id = {0} AND c.original_source_id = {1} AND c.copy_published_at >= {2} ORDER BY c.copy_published_at DESC LIMIT 20",
            copierId, originalId, since).ToListAsync(ct);
        return PairDelays.Summarize(copierId, originalId, days, rows, recent.Select(ToDto).ToList());
    }

    /// <summary>Kyiv calendar days: the period starts at local midnight `days - 1` days ago.</summary>
    private (DateOnly SinceDay, DateTime SinceUtc) Period(int days)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Kyiv).DateTime);
        var sinceDay = today.AddDays(-(days - 1));
        return (sinceDay, TimeZoneInfo.ConvertTimeToUtc(sinceDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Kyiv));
    }

    private static RecentCopyDto ToDto(RecentRow r) => new(r.CopierId, r.CopyRawMessageId, Utc(r.CopyPublishedAt), r.CopyText, r.CopyUrl,
        r.OriginalId, r.OriginalRawMessageId, Utc(r.OriginalPublishedAt), r.OriginalText, r.OriginalUrl,
        r.DelaySeconds, Math.Round(r.Jaccard, 3), Math.Round(r.Containment, 3), ((CopyKind)r.Kind).ToString().ToLowerInvariant(), r.IsPrimary);

    private const string RecentSelect = """
        SELECT c.copy_source_id AS copier_id, c.copy_raw_message_id, c.copy_published_at, left(cm.raw_text, 300) AS copy_text, cm.url AS copy_url,
               c.original_source_id AS original_id, c.original_raw_message_id, c.original_published_at, left(om.raw_text, 300) AS original_text, om.url AS original_url,
               c.delay_seconds, c.jaccard::float AS jaccard, c.containment::float AS containment, c.kind, c.is_primary
        FROM analytics.copies c
        JOIN raw_messages cm ON cm.raw_message_id = c.copy_raw_message_id
        JOIN raw_messages om ON om.raw_message_id = c.original_raw_message_id
        """;

    /// <summary>Drops the index and the pairs and rewinds the watermark; the service rebuilds everything on its next run.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.copies", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.messages", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.track_firsts", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.state WHERE key NOT LIKE 'lifecycle_%'", ct); // P15: the lifecycle projection has its own cursor/report (review N2)
        await tx.CommitAsync(ct);
    }

    public static async Task<bool> InitializedAsync(AnalyticsDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('analytics.runs') IS NOT NULL AS \"Value\"").ToListAsync(ct)).First();

    private async Task<DateTimeOffset?> HeartbeatAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var key = $"Runtime:Worker:{options.Value.Name}:Heartbeat";
        var value = (await db.Database.SqlQuery<string>($"SELECT value AS \"Value\" FROM app_settings WHERE key = {key}").ToListAsync(ct)).FirstOrDefault();
        return DateTimeOffset.TryParse(value, out var at) ? at : null;
    }

    /// <summary>The status document the analytics process writes next to its heartbeat (docs/plan-admin-ops.md §2.1); null until it exists or when it does not parse.</summary>
    private async Task<AnalyticsInstanceDto?> InstanceAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var key = $"Runtime:Worker:{options.Value.Name}:Status";
        var value = (await db.Database.SqlQuery<string>($"SELECT value AS \"Value\" FROM app_settings WHERE key = {key}").ToListAsync(ct)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            var doc = JsonSerializer.Deserialize<StatusDocument>(value, StatusJson);
            return doc is null ? null : new AnalyticsInstanceDto(doc.Host, doc.Version, doc.BuiltAt, doc.StartedAt, doc.At, doc.Pid, doc.WorkingSetBytes, doc.CpuPercent, doc.Threads);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web);

    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private static RunDto ToDto(AnalysisRun r) => new(r.RunId, r.Instance, r.StartedAt, r.FinishedAt, r.UpdatedAt, r.Status.ToString().ToLowerInvariant(),
        r.WatermarkFrom, r.WatermarkTo, r.MessagesScanned, r.MessagesFingerprinted, r.PairsFound, r.Error);

    /// <summary>The subset of Puluj.Contracts.WorkerStatusDto this library reads (it does not reference Contracts).</summary>
    private sealed record StatusDocument(string? Host, string? Version, DateTimeOffset? BuiltAt, DateTimeOffset? StartedAt, DateTimeOffset? At, int? Pid, long? WorkingSetBytes, double? CpuPercent, int? Threads);
    private sealed record CountRow(long Messages, long Fingerprinted, long Pairs, long SchemaBytes);
    private sealed record SourceRow(int SourceId, string Code, string Name, bool Enabled);
    private sealed record PostDayRow(int SourceId, DateOnly Day, int Posts, int RowCount);
    private sealed record HourRow(int SourceId, int Hour, int Posts);
    private sealed record CopierDayRow(int SourceId, DateOnly Day, int Copies, double AvgDelay, double MedianDelay, int Verbatim);
    private sealed record OriginalDayRow(int SourceId, DateOnly Day, int CopiedBy, double AvgLead);
    private sealed record ForwardRow(int SourceId, int Internal, int External);
    private sealed record PairRow(int CopierId, int OriginalId, int PairCount, int CountAll, int Verbatim, int Forwards, double AvgDelay, double MedianDelay, double MinDelay, double AvgJaccard);
    private sealed record ExternalRow(int SourceId, string ChannelRef, int Forwards);
    private sealed record FirstRow(int SourceId, string CategoryCode, int Firsts, int Participations, double LagSum, int LagCount);
    private sealed record RecentRow(int CopierId, long CopyRawMessageId, DateTime CopyPublishedAt, string? CopyText, string? CopyUrl,
        int OriginalId, long OriginalRawMessageId, DateTime OriginalPublishedAt, string? OriginalText, string? OriginalUrl,
        double DelaySeconds, double Jaccard, double Containment, int Kind, bool IsPrimary);
}
