using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>Server-side analytics: every tab shares one typed evidence predicate and a bounded, canonical cache key.</summary>
public sealed class StatsService(IDbContextFactory<PulujDbContext> factory, ReferenceCache refs, IMemoryCache cache, TimeProvider clock)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const int TopRegions = 15, MaxRoutes = 400, TopDays = 10;
    private const string Tz = "Europe/Kyiv";
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new(StringComparer.Ordinal);

    private sealed record BucketCategoryRow(DateTimeOffset BucketAt, int? CategoryId, long N);
    private sealed record ClassRow(int? ClassId, int? CategoryId, long N, long? Objects);
    private sealed record PlaceRow(int? PlaceId, long N);
    private sealed record RouteRow(int OriginPlaceId, int DestinationPlaceId, long N);
    private sealed record HourRow(int Dow, int Hour, long N);
    private sealed record ValueRow(int Value, long N);
    private sealed record AlertRow(int PlaceId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
    private sealed record SourceRow(int SourceId, long Messages, long Processed, long WithTargets, double? MedianLag);
    private sealed record SourceBucketRow(int SourceId, DateTimeOffset BucketAt, long N);
    private sealed record SourceTargetsRow(int SourceId, long N);
    private sealed record MessageBucketRow(DateTimeOffset BucketAt, long Processed, long WithTargets);

    public Task<StatsTargetsDto> TargetsAsync(DateTimeOffset? from, DateTimeOffset? to, StatsFilter filter, CancellationToken ct) => CachedAsync("targets", from, to, filter, ComputeTargetsAsync, ct);
    public Task<StatsAlertsDto> AlertsAsync(DateTimeOffset? from, DateTimeOffset? to, StatsFilter filter, CancellationToken ct) => CachedAsync("alerts", from, to, filter, ComputeAlertsAsync, ct);
    public Task<StatsSourcesDto> SourcesAsync(DateTimeOffset? from, DateTimeOffset? to, StatsFilter filter, CancellationToken ct) => CachedAsync("sources", from, to, filter, ComputeSourcesAsync, ct);
    public Task<StatsRecognitionDto> RecognitionAsync(DateTimeOffset? from, DateTimeOffset? to, StatsFilter filter, CancellationToken ct) => CachedAsync("recognition", from, to, filter, ComputeRecognitionAsync, ct);

    private async Task<T> CachedAsync<T>(string section, DateTimeOffset? from, DateTimeOffset? to, StatsFilter filter, Func<Period, StatsFilter, CancellationToken, Task<T>> compute, CancellationToken ct)
    {
        var (start, end) = StatsBuckets.Clamp(from, to, clock.GetUtcNow());
        var generation = await ActiveGenerationKeyAsync(ct);
        var key = $"stats|{section}|live|generation={generation}|{start:O}|{end:O}|{filter.CacheKey}";
        Task<object> task;
        if (!cache.TryGetValue(key, out Task<object>? cached) || cached is null)
        {
            var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => (object)(await compute(Period.Of(start, end), filter, CancellationToken.None))!));
            task = lazy.Value;
            _ = cache.Set(key, task, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheTtl });
            _ = task.ContinueWith(completed =>
            {
                _inflight.TryRemove(key, out _);
                if (completed.IsFaulted || completed.IsCanceled) cache.Remove(key);
            }, TaskScheduler.Default);
        }
        else task = cached;
        return (T)await task.WaitAsync(ct);
    }

    private sealed record Period(DateTimeOffset From, DateTimeOffset To, StatsBucket Bucket, string Unit, List<DateTimeOffset> Starts)
    {
        public static Period Of(DateTimeOffset from, DateTimeOffset to)
        {
            var bucket = StatsBuckets.BucketFor(to - from);
            return new(from, to, bucket, StatsBuckets.UnitName(bucket), StatsBuckets.Starts(from, to, bucket));
        }
        public StatsPeriodDto Dto => new(From, To, Unit, Starts);
        public long[] Series<TRow>(IEnumerable<TRow> rows, Func<TRow, DateTimeOffset> at, Func<TRow, long> n)
        {
            var series = new long[Starts.Count];
            foreach (var row in rows) if (Starts.Count > 0) series[StatsBuckets.IndexOf(Starts, at(row))] += n(row);
            return series;
        }
    }

    private async Task<StatsTargetsDto> ComputeTargetsAsync(Period p, StatsFilter f, CancellationToken ct)
    {
        await refs.Ready.WaitAsync(ct);
        var categories = StatsFolds.CategoryOrder.Length;
        var categoryIndex = refs.Categories.Values.ToDictionary(c => c.TargetCategoryId, c => StatsFolds.CategoryIndex(c.Code));
        var cat = (int? id) => id is int value && categoryIndex.TryGetValue(value, out var index) ? index : categories - 1;
        var facts = $"t.observed_at >= {Ts(p.From)} AND t.observed_at < {Ts(p.To)} AND t.event_type = 1{f.TargetPredicate("t", true)}";
        var tracks = $"tt.first_seen_at >= {Ts(p.From)} AND tt.first_seen_at < {Ts(p.To)}{f.TrackPredicate("tt")}";
        await using var db = await factory.CreateDbContextAsync(ct);
        var targetBuckets = await Rows<BucketCategoryRow>(db, $"SELECT date_trunc('{p.Unit}', t.observed_at, '{Tz}') AS bucket_at, t.target_category_id AS category_id, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1,2", ct);
        var trackBuckets = await Rows<BucketCategoryRow>(db, $"SELECT date_trunc('{p.Unit}', tt.first_seen_at, '{Tz}') AS bucket_at, tt.target_category_id AS category_id, count(*) AS n FROM target_tracks tt WHERE {tracks} GROUP BY 1,2", ct);
        var classRows = await Rows<ClassRow>(db, $"SELECT t.target_class_id AS class_id, t.target_category_id AS category_id, count(*) AS n, sum(t.object_count) AS objects FROM targets t WHERE {facts} GROUP BY 1,2", ct);
        var trackClassRows = await Rows<ClassRow>(db, $"SELECT tt.target_class_id AS class_id, tt.target_category_id AS category_id, count(*) AS n, NULL::bigint AS objects FROM target_tracks tt WHERE {tracks} GROUP BY 1,2", ct);
        var placeRows = await Rows<PlaceRow>(db, $"SELECT t.location_place_id AS place_id, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1", ct);
        var routeRows = await Rows<RouteRow>(db, $"SELECT t.origin_place_id AS origin_place_id, t.destination_place_id AS destination_place_id, count(*) AS n FROM targets t WHERE {facts} AND t.origin_place_id IS NOT NULL AND t.destination_place_id IS NOT NULL GROUP BY 1,2", ct);
        var hourRows = await Rows<HourRow>(db, $"SELECT CAST(extract(dow FROM t.observed_at AT TIME ZONE '{Tz}') AS int) AS dow, CAST(extract(hour FROM t.observed_at AT TIME ZONE '{Tz}') AS int) AS hour, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1,2", ct);
        var targetSeries = EmptyMatrix(p.Starts.Count, categories);
        var trackSeries = EmptyMatrix(p.Starts.Count, categories);
        foreach (var row in targetBuckets) targetSeries[StatsBuckets.IndexOf(p.Starts, row.BucketAt)][cat(row.CategoryId)] += row.N;
        foreach (var row in trackBuckets) trackSeries[StatsBuckets.IndexOf(p.Starts, row.BucketAt)][cat(row.CategoryId)] += row.N;
        var tracksByClass = trackClassRows.GroupBy(r => (r.ClassId, r.CategoryId)).ToDictionary(g => g.Key, g => g.Sum(r => r.N));
        var byClass = classRows.Select(r =>
        {
            var cls = r.ClassId is int classId ? refs.Classes.GetValueOrDefault(classId) : null;
            var category = r.CategoryId is int categoryId ? refs.Categories.GetValueOrDefault(categoryId) : null;
            return new StatsClassDto(cls?.Code ?? $"{category?.Code ?? "UNKNOWN"}_UNCLASSIFIED", cls?.Name ?? (category is null ? "Невідома загроза" : $"{category.Name} (без класу)"), StatsFolds.CategoryOrder[cat(r.CategoryId)], r.N, tracksByClass.GetValueOrDefault((r.ClassId, r.CategoryId)), r.Objects ?? 0);
        }).OrderByDescending(x => x.Targets).ToList();
        var hourWeekday = EmptyMatrix(7, 24);
        foreach (var row in hourRows) hourWeekday[(row.Dow + 6) % 7][row.Hour] += row.N;
        var (byRegion, unlocated) = StatsFolds.ByRegion(placeRows.Select(r => (r.PlaceId, r.N)), refs.RegionOf, TopRegions);
        return new StatsTargetsDto(p.Dto, f.Meta("targets"), targetBuckets.Sum(r => r.N), trackBuckets.Sum(r => r.N), classRows.Sum(r => r.Objects ?? 0),
            StatsFolds.CategoryOrder.Select(code => new StatsCategoryDto(code, refs.Categories.Values.FirstOrDefault(x => x.Code == code)?.Name ?? code)).ToList(), targetSeries, trackSeries, byClass, byRegion, unlocated,
            StatsFolds.Routes(routeRows.Select(r => (r.OriginPlaceId, r.DestinationPlaceId, r.N)), refs.RegionOf, MaxRoutes), hourWeekday);
    }

    private async Task<StatsAlertsDto> ComputeAlertsAsync(Period p, StatsFilter f, CancellationToken ct)
    {
        await refs.Ready.WaitAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await Rows<AlertRow>(db, $"SELECT a.place_id AS place_id, a.started_at AS started_at, a.ended_at AS ended_at FROM air_alerts a WHERE a.started_at < {Ts(p.To)} AND (a.ended_at IS NULL OR a.ended_at > {Ts(p.From)}){f.AlertPredicate("a")}", ct);
        var summary = AlertIntervals.Summarize(rows.Select(x => new AlertInterval(x.PlaceId, x.StartedAt, x.EndedAt)), p.From, p.To, p.Starts, id => refs.Place(id));
        return new StatsAlertsDto(p.Dto, f.Meta("alerts"), summary.Count, summary.Hours, summary.OpenAtEnd, summary.DeclaredPerBucket, summary.HoursPerBucket, summary.ByRegion, summary.Durations, summary.DeclaredByHour,
            summary.Days.Where(x => x.Hours > 0).OrderByDescending(x => x.Hours).ThenBy(x => x.Day).Take(TopDays).ToList());
    }

    private async Task<StatsSourcesDto> ComputeSourcesAsync(Period p, StatsFilter f, CancellationToken ct)
    {
        await refs.Ready.WaitAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = $"r.published_at >= {Ts(p.From)} AND r.published_at < {Ts(p.To)}{f.RawPredicate("r")}";
        var facts = $"t.observed_at >= {Ts(p.From)} AND t.observed_at < {Ts(p.To)} AND t.event_type = 1{f.TargetPredicate("t", true)}";
        var sourceRows = await Rows<SourceRow>(db, $"SELECT r.source_id AS source_id, count(*) AS messages, count(*) FILTER (WHERE r.processing_status = 1) AS processed, count(*) FILTER (WHERE r.processing_status = 1 AND EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = r.raw_message_id)) AS with_targets, percentile_cont(0.5) WITHIN GROUP (ORDER BY CAST(extract(epoch FROM r.received_at-r.published_at) AS float8)) FILTER (WHERE r.received_at >= r.published_at AND r.received_at-r.published_at < interval '6 hours') AS median_lag FROM raw_messages r WHERE {raw} GROUP BY 1", ct);
        var buckets = await Rows<SourceBucketRow>(db, $"SELECT r.source_id AS source_id, date_trunc('{p.Unit}', r.published_at, '{Tz}') AS bucket_at, count(*) AS n FROM raw_messages r WHERE {raw} GROUP BY 1,2", ct);
        var targets = await Rows<SourceTargetsRow>(db, $"SELECT t.source_id AS source_id, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1", ct);
        var series = buckets.GroupBy(x => x.SourceId).ToDictionary(g => g.Key, g => p.Series(g, x => x.BucketAt, x => x.N));
        var targetBySource = targets.ToDictionary(x => x.SourceId, x => x.N);
        // Raw-message rows are ordered by PublishedAt.  Facts use ObservedAt
        // and must not disappear simply because the source had no raw revision
        // in this publication window, so retain their source rows with zeroed
        // raw counters when necessary.
        var sourceIds = sourceRows.Select(x => x.SourceId).Concat(targetBySource.Keys).Distinct();
        var rawBySource = sourceRows.ToDictionary(x => x.SourceId);
        var sources = sourceIds.Select(sourceId =>
        {
            rawBySource.TryGetValue(sourceId, out var row);
            var source = refs.Sources.GetValueOrDefault(sourceId);
            return new StatsSourceDto(sourceId, source?.Code ?? $"#{sourceId}", source?.Name ?? $"#{sourceId}", row?.Messages ?? 0, row?.Processed ?? 0, row?.WithTargets ?? 0, targetBySource.GetValueOrDefault(sourceId), row?.MedianLag is { } lag ? Math.Round(lag) : null, series.GetValueOrDefault(sourceId) ?? new long[p.Starts.Count]);
        }).OrderByDescending(x => x.Messages).ThenByDescending(x => x.Targets).ToList();
        // This total is deliberately sourced from the observedAt aggregate,
        // not from the PublishedAt source rows above.
        return new StatsSourcesDto(p.Dto, f.Meta("sources"), f.Meta("targets"), sources.Sum(x => x.Messages), sources.Sum(x => x.Processed), sources.Sum(x => x.WithTargets), targets.Sum(x => x.N), sources);
    }

    private async Task<StatsRecognitionDto> ComputeRecognitionAsync(Period p, StatsFilter f, CancellationToken ct)
    {
        await refs.Ready.WaitAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = $"t.observed_at >= {Ts(p.From)} AND t.observed_at < {Ts(p.To)}{f.TargetPredicate("t", true)}";
        var facts = all + " AND t.event_type = 1";
        var raw = $"r.published_at >= {Ts(p.From)} AND r.published_at < {Ts(p.To)}{f.RawPredicate("r")}";
        var events = await Rows<ValueRow>(db, $"SELECT t.event_type AS value, count(*) AS n FROM targets t WHERE {all} GROUP BY 1", ct);
        var methods = await Rows<ValueRow>(db, $"SELECT t.identification_method AS value, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1", ct);
        var confidence = await Rows<ValueRow>(db, $"SELECT t.confidence AS value, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1", ct);
        var locations = await Rows<ValueRow>(db, $"SELECT t.location_kind AS value, count(*) AS n FROM targets t WHERE {facts} GROUP BY 1", ct);
        var messages = await Rows<MessageBucketRow>(db, $"SELECT date_trunc('{p.Unit}', r.published_at, '{Tz}') AS bucket_at, count(*) FILTER (WHERE r.processing_status = 1) AS processed, count(*) FILTER (WHERE r.processing_status = 1 AND EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = r.raw_message_id)) AS with_targets FROM raw_messages r WHERE {raw} GROUP BY 1", ct);
        return new StatsRecognitionDto(p.Dto, f.Meta("recognition"), methods.Sum(x => x.N), messages.Sum(x => x.Processed), messages.Sum(x => x.WithTargets),
            StatsFolds.Slices(events.Select(x => (x.Value, x.N)), StatsFolds.EventTypeLabels), StatsFolds.Slices(methods.Select(x => (x.Value, x.N)), StatsFolds.MethodLabels), StatsFolds.Slices(confidence.Select(x => (x.Value, x.N)), StatsFolds.ConfidenceLabels), StatsFolds.Slices(locations.Select(x => (x.Value, x.N)), StatsFolds.LocationKindLabels),
            p.Series(messages, x => x.BucketAt, x => x.Processed), p.Series(messages, x => x.BucketAt, x => x.WithTargets));
    }

    private static Task<List<T>> Rows<T>(PulujDbContext db, string sql, CancellationToken ct) => db.Database.SqlQueryRaw<T>(sql).ToListAsync(ct);
    private async Task<string> ActiveGenerationKeyAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var active = await db.ProcessingGenerations.AsNoTracking().Where(x => x.IsActive).Select(x => x.GenerationId).OrderBy(x => x).ToListAsync(ct);
        return active.Count == 0 ? "none" : string.Join(',', active);
    }
    private static long[][] EmptyMatrix(int rows, int columns) => Enumerable.Range(0, rows).Select(_ => new long[columns]).ToArray();
    private static string Ts(DateTimeOffset value) => $"TIMESTAMPTZ '{value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'";
}
