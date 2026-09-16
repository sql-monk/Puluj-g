using Puluj.Contracts;
using Puluj.Domain.Enums;

namespace Puluj.Api.Services;

/// <summary>Air-raid alert interval as stored: place, start, end (null while open).</summary>
public sealed record AlertInterval(int PlaceId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>Everything the alerts tab derives from the intervals of a period (see <see cref="AlertIntervals.Summarize"/>).</summary>
public sealed record AlertSummary(
    long Count,
    double Hours,
    long OpenAtEnd,
    long[] DeclaredPerBucket,
    double[] HoursPerBucket,
    IReadOnlyList<StatsAlertRegionDto> ByRegion,
    IReadOnlyList<StatsSliceDto> Durations,
    long[] DeclaredByHour,
    IReadOnlyList<StatsAlertDayDto> Days);

/// <summary>
/// The arithmetic of alert intervals, pure: clipping to the period, splitting an interval at bucket (and Kyiv day)
/// borders, the duration histogram, and the region filter. No database, so it is unit-tested on its own.
/// </summary>
public static class AlertIntervals
{
    public static readonly (string Key, string Label, double MaxMinutes)[] DurationBins =
    [
        ("lt30", "до 30 хв", 30),
        ("30to60", "30–60 хв", 60),
        ("1to2h", "1–2 год", 120),
        ("2to4h", "2–4 год", 240),
        ("4to8h", "4–8 год", 480),
        ("gt8h", "понад 8 год", double.PositiveInfinity),
    ];

    /// <summary>Only alerts declared for a whole oblast (or Kyiv city) are comparable between regions and summable in hours.</summary>
    public static bool IsRegionLevel(ReferenceCache.PlaceInfo? place) =>
        place is not null && (place.Level == PlaceLevel.Region || (place.Level == PlaceLevel.City && place.ParentId is null));

    /// <summary>The part of an interval inside [from, to); null when nothing of it is (an open interval runs to `to`).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? Clip(AlertInterval a, DateTimeOffset from, DateTimeOffset to)
    {
        var start = a.StartedAt > from ? a.StartedAt : from;
        var end = a.EndedAt is { } e && e < to ? e : to;
        return end > start ? (start, end) : null;
    }

    /// <summary>Bin index of a whole duration; bins are left-closed (exactly 30 min is "30–60").</summary>
    public static int DurationBin(double minutes) => Array.FindIndex(DurationBins, b => minutes < b.MaxMinutes);

    /// <summary>
    /// Adds the hours of [start, end) to the buckets it crosses: an interval is cut at every bucket start, the last
    /// bucket ends at `to`. Anything before the first bucket lands in it.
    /// </summary>
    public static void SplitHours(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<DateTimeOffset> buckets, DateTimeOffset to, double[] into)
    {
        if (buckets.Count == 0)
        {
            return;
        }
        var i = StatsBuckets.IndexOf(buckets, start);
        for (var t = start; t < end && i < buckets.Count; i++)
        {
            var next = i + 1 < buckets.Count ? buckets[i + 1] : to;
            var stop = next < end ? next : end;
            if (stop > t)
            {
                into[i] += (stop - t).TotalHours;
            }
            t = stop;
        }
    }

    /// <summary>
    /// Region-level alerts of a period: totals, per bucket (declared inside the bucket; hours under alert with the
    /// interval split at bucket borders), per region, the duration histogram of the ended ones (whole duration, not
    /// clipped), declarations per hour of the day and per Kyiv day (every day of the period, in order).
    /// </summary>
    public static AlertSummary Summarize(IEnumerable<AlertInterval> intervals, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<DateTimeOffset> buckets, Func<int, ReferenceCache.PlaceInfo?> place)
    {
        var days = StatsBuckets.Starts(from, to, StatsBucket.Day);
        var byRegion = new Dictionary<int, (string Name, long Count, double Hours)>();
        var clippedByRegion = new Dictionary<int, List<(DateTimeOffset Start, DateTimeOffset End)>>();
        var durations = new long[DurationBins.Length];
        var declaredPerBucket = new long[buckets.Count];
        var hoursPerBucket = new double[buckets.Count];
        var declaredByHour = new long[24];
        var declaredPerDay = new long[days.Count];
        var hoursPerDay = new double[days.Count];
        long count = 0;
        long open = 0;
        var hours = 0.0;
        foreach (var a in intervals)
        {
            var p = place(a.PlaceId);
            if (!IsRegionLevel(p) || Clip(a, from, to) is not var (start, end))
            {
                continue;
            }
            count++;
            if (a.EndedAt is null || a.EndedAt > to)
            {
                open++;
            }
            var cur = byRegion.GetValueOrDefault(p!.Id);
            byRegion[p.Id] = (p.Name, cur.Count + 1, cur.Hours);
            if (!clippedByRegion.TryGetValue(p.Id, out var clipped)) clippedByRegion[p.Id] = clipped = [];
            clipped.Add((start, end));
            if (a.EndedAt is { } ended)
            {
                durations[DurationBin((ended - a.StartedAt).TotalMinutes)]++;
            }
            if (a.StartedAt >= from && a.StartedAt < to)
            {
                if (buckets.Count > 0)
                {
                    declaredPerBucket[StatsBuckets.IndexOf(buckets, a.StartedAt)]++;
                }
                declaredByHour[StatsBuckets.HourOfDay(a.StartedAt)]++;
                declaredPerDay[StatsBuckets.IndexOf(days, a.StartedAt)]++;
            }
        }
        // A source can revise or repeat a state interval.  Declarations remain individual facts, but coverage is the
        // union per whole region: overlapping/nested intervals never inflate oblast-hours.
        foreach (var (regionId, clippedIntervals) in clippedByRegion)
        {
            foreach (var (start, end) in Union(clippedIntervals))
            {
                var h = (end - start).TotalHours;
                hours += h;
                var row = byRegion[regionId];
                byRegion[regionId] = (row.Name, row.Count, row.Hours + h);
                SplitHours(start, end, buckets, to, hoursPerBucket);
                SplitHours(start, end, days, to, hoursPerDay);
            }
        }
        var regions = byRegion.OrderByDescending(kv => kv.Value.Hours).ThenBy(kv => kv.Value.Name)
            .Select(kv => new StatsAlertRegionDto(kv.Key, kv.Value.Name, kv.Value.Count, Round(kv.Value.Hours)))
            .ToList();
        var bins = DurationBins.Select((b, i) => new StatsSliceDto(b.Key, b.Label, durations[i])).ToList();
        var dayRows = days.Select((d, i) => new StatsAlertDayDto(StatsBuckets.DayOf(d), declaredPerDay[i], Round(hoursPerDay[i]))).ToList();
        return new AlertSummary(count, Round(hours), open, declaredPerBucket, hoursPerBucket.Select(Round).ToArray(), regions, bins, declaredByHour, dayRows);
    }

    private static double Round(double hours) => Math.Round(hours, 2);

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Union(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var ordered = intervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        if (ordered.Count == 0) yield break;
        var current = ordered[0];
        foreach (var next in ordered.Skip(1))
        {
            if (next.Start <= current.End)
            {
                if (next.End > current.End) current = (current.Start, next.End);
            }
            else
            {
                yield return current;
                current = next;
            }
        }
        yield return current;
    }
}
