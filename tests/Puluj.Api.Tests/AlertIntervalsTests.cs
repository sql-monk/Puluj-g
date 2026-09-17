using Puluj.Api.Services;
using Puluj.Domain.Enums;

namespace Puluj.Api.Tests;

public class AlertIntervalsTests
{
    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private static ReferenceCache.PlaceInfo Place(int id, string name, PlaceLevel level, int? parent = null) =>
        new(id, name, level, parent, "UA", 30, 50, 10, 0);

    private static readonly Dictionary<int, ReferenceCache.PlaceInfo> Places = new()
    {
        [1] = Place(1, "Сумська область", PlaceLevel.Region),
        [2] = Place(2, "Київ", PlaceLevel.City),
        [3] = Place(3, "Конотопська громада", PlaceLevel.Hromada, 1),
    };

    private static ReferenceCache.PlaceInfo? PlaceOf(int id) => Places.GetValueOrDefault(id);

    [Fact]
    public void Clip_CutsAtThePeriodAndRunsOpenIntervalsToTheEnd()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-14T04:00:00Z");
        Assert.Equal((from, Utc("2026-09-14T01:00:00Z")), AlertIntervals.Clip(new AlertInterval(1, Utc("2026-09-13T23:00:00Z"), Utc("2026-09-14T01:00:00Z")), from, to));
        Assert.Equal((Utc("2026-09-14T03:00:00Z"), to), AlertIntervals.Clip(new AlertInterval(1, Utc("2026-09-14T03:00:00Z"), null), from, to));
        Assert.Equal((Utc("2026-09-14T01:00:00Z"), to), AlertIntervals.Clip(new AlertInterval(1, Utc("2026-09-14T01:00:00Z"), Utc("2026-09-14T09:00:00Z")), from, to));
        Assert.Null(AlertIntervals.Clip(new AlertInterval(1, Utc("2026-09-13T20:00:00Z"), Utc("2026-09-13T21:00:00Z")), from, to));
        Assert.Null(AlertIntervals.Clip(new AlertInterval(1, Utc("2026-09-14T04:00:00Z"), null), from, to)); // starts exactly at `to`
    }

    [Fact]
    public void SplitHours_CutsAnIntervalAtBucketBorders()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-14T04:00:00Z");
        var buckets = StatsBuckets.Starts(from, to, StatsBucket.Hour);
        var hours = new double[buckets.Count];
        AlertIntervals.SplitHours(Utc("2026-09-14T01:30:00Z"), Utc("2026-09-14T03:15:00Z"), buckets, to, hours);
        Assert.Equal([0, 0.5, 1, 0.25], hours);
        // Before the first bucket: everything lands in it; nothing is lost past the last one.
        AlertIntervals.SplitHours(Utc("2026-09-13T23:00:00Z"), Utc("2026-09-14T00:30:00Z"), buckets, to, hours);
        Assert.Equal(1.5, hours[0]);
    }

    [Fact]
    public void Summarize_ClipsToThePeriodSplitsHoursByBucketAndKeepsOnlyRegionLevel()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-14T04:00:00Z");
        var starts = StatsBuckets.Starts(from, to, StatsBucket.Hour);
        var intervals = new[]
        {
            // Started before the period, ended inside: 1 h inside (00:00–01:00), whole duration 2 h.
            new AlertInterval(1, Utc("2026-09-13T23:00:00Z"), Utc("2026-09-14T01:00:00Z")),
            // Inside, spans two buckets: 01:30–02:30.
            new AlertInterval(2, Utc("2026-09-14T01:30:00Z"), Utc("2026-09-14T02:30:00Z")),
            // Still open: clipped at `to`, 03:00–04:00, not in the duration histogram.
            new AlertInterval(1, Utc("2026-09-14T03:00:00Z"), null),
            // Hromada level: ignored.
            new AlertInterval(3, Utc("2026-09-14T00:00:00Z"), Utc("2026-09-14T03:00:00Z")),
            // Ended before the period: ignored.
            new AlertInterval(1, Utc("2026-09-13T20:00:00Z"), Utc("2026-09-13T21:00:00Z")),
        };
        var s = AlertIntervals.Summarize(intervals, from, to, starts, PlaceOf);

        Assert.Equal(3, s.Count);
        Assert.Equal(3.0, s.Hours);
        Assert.Equal(1, s.OpenAtEnd);
        Assert.Equal([0, 1, 0, 1], s.DeclaredPerBucket); // only starts inside the period count
        Assert.Equal([1.0, 0.5, 0.5, 1.0], s.HoursPerBucket);
        Assert.Equal(2, s.ByRegion.Count);
        Assert.Equal(("Сумська область", 2, 2.0), (s.ByRegion[0].Name, s.ByRegion[0].Count, s.ByRegion[0].Hours));
        Assert.Equal(("Київ", 1, 1.0), (s.ByRegion[1].Name, s.ByRegion[1].Count, s.ByRegion[1].Hours));
        // Bins are left-closed: the 60 min alert is "1–2 h", the 120 min one "2–4 h"; the open alert has no duration yet.
        Assert.Equal(1, s.Durations.Single(d => d.Key == "1to2h").Count);
        Assert.Equal(1, s.Durations.Single(d => d.Key == "2to4h").Count);
        Assert.Equal(2, s.Durations.Sum(d => d.Count));
        // Declared at 01:30Z = 04:30 Kyiv and 03:00Z = 06:00 Kyiv.
        Assert.Equal(1, s.DeclaredByHour[4]);
        Assert.Equal(1, s.DeclaredByHour[6]);
        Assert.Equal(2, s.DeclaredByHour.Sum());
        // The period lies inside one Kyiv day (03:00–07:00 local).
        var day = Assert.Single(s.Days);
        Assert.Equal((new DateOnly(2026, 9, 14), 2, 3.0), (day.Day, day.Count, day.Hours));
    }

    [Fact]
    public void Summarize_DurationBinsAreLeftClosed()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-15T00:00:00Z");
        var intervals = new[]
        {
            new AlertInterval(1, Utc("2026-09-14T00:00:00Z"), Utc("2026-09-14T00:29:00Z")),
            new AlertInterval(1, Utc("2026-09-14T01:00:00Z"), Utc("2026-09-14T01:30:00Z")),
            new AlertInterval(1, Utc("2026-09-14T02:00:00Z"), Utc("2026-09-14T03:00:00Z")),
            new AlertInterval(1, Utc("2026-09-14T04:00:00Z"), Utc("2026-09-14T13:00:00Z")),
        };
        var s = AlertIntervals.Summarize(intervals, from, to, StatsBuckets.Starts(from, to, StatsBucket.Day), PlaceOf);
        Assert.Equal([1, 1, 1, 0, 0, 1], s.Durations.Select(d => d.Count));
        Assert.Equal(0, AlertIntervals.DurationBin(0));
        Assert.Equal(1, AlertIntervals.DurationBin(30));
        Assert.Equal(5, AlertIntervals.DurationBin(480));
    }

    [Fact]
    public void Summarize_DstDayHas23Hours()
    {
        // Ukraine switched to summer time on 2025-03-30 (03:00 → 04:00): that local day is 23 hours long.
        var from = Utc("2025-03-29T22:00:00Z"); // 2025-03-30 00:00 +02
        var to = Utc("2025-03-30T21:00:00Z"); // 2025-03-31 00:00 +03
        var buckets = StatsBuckets.Starts(from, to, StatsBucket.Day);
        var day = Assert.Single(buckets);
        Assert.Equal(from, day);
        var s = AlertIntervals.Summarize([new AlertInterval(1, Utc("2025-03-29T20:00:00Z"), null)], from, to, buckets, PlaceOf);
        Assert.Equal([23.0], s.HoursPerBucket);
        Assert.Equal(23.0, s.Hours);
        Assert.Equal((new DateOnly(2025, 3, 30), 23.0), (s.Days[0].Day, s.Days[0].Hours));
    }

    [Fact]
    public void Summarize_DstDayHas25Hours_AndSplitsAcrossTheHourBucketsOfTheChange()
    {
        // Back to winter time on 2025-10-26 (04:00 → 03:00): 25 local hours between the two midnights.
        var from = Utc("2025-10-25T21:00:00Z"); // 2025-10-26 00:00 +03
        var to = Utc("2025-10-26T22:00:00Z"); // 2025-10-27 00:00 +02
        var days = StatsBuckets.Starts(from, to, StatsBucket.Day);
        Assert.Single(days);
        var s = AlertIntervals.Summarize([new AlertInterval(2, from, to)], from, to, days, PlaceOf);
        Assert.Equal([25.0], s.HoursPerBucket);
        // Hour buckets are UTC hours: the repeated local hour is two buckets, nothing is doubled.
        var hours = StatsBuckets.Starts(from, to, StatsBucket.Hour);
        Assert.Equal(25, hours.Count);
        var h = AlertIntervals.Summarize([new AlertInterval(2, Utc("2025-10-26T00:30:00Z"), Utc("2025-10-26T02:30:00Z"))], from, to, hours, PlaceOf);
        Assert.Equal(2.0, h.HoursPerBucket.Sum());
        Assert.Equal(0.5, h.HoursPerBucket[3]);
        Assert.Equal(1.0, h.HoursPerBucket[4]);
        Assert.Equal(0.5, h.HoursPerBucket[5]);
    }

    [Fact]
    public void Summarize_WithoutBucketsStillCountsTotals()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-14T04:00:00Z");
        var s = AlertIntervals.Summarize([new AlertInterval(1, from, null)], from, to, [], PlaceOf);
        Assert.Equal(1, s.Count);
        Assert.Equal(4.0, s.Hours);
        Assert.Empty(s.HoursPerBucket);
    }

    [Fact]
    public void Summarize_UsesTheUnionOfOverlappingIntervalsPerRegion()
    {
        var from = Utc("2026-09-14T00:00:00Z");
        var to = Utc("2026-09-14T05:00:00Z");
        var starts = StatsBuckets.Starts(from, to, StatsBucket.Hour);
        var s = AlertIntervals.Summarize(
        [
            new AlertInterval(1, Utc("2026-09-14T00:00:00Z"), Utc("2026-09-14T03:00:00Z")),
            new AlertInterval(1, Utc("2026-09-14T01:00:00Z"), Utc("2026-09-14T04:00:00Z")),
            new AlertInterval(1, Utc("2026-09-14T02:00:00Z"), Utc("2026-09-14T02:30:00Z")),
        ], from, to, starts, PlaceOf);

        Assert.Equal(3, s.Count); // declarations remain individual evidence records
        Assert.Equal(4.0, s.Hours); // coverage is [00:00, 04:00), not 6.5 accumulated hours
        Assert.Equal([1.0, 1.0, 1.0, 1.0, 0.0], s.HoursPerBucket);
        Assert.Equal(4.0, Assert.Single(s.ByRegion).Hours);
    }

    [Fact]
    public void IsRegionLevel_AcceptsOblastsAndParentlessCities()
    {
        Assert.True(AlertIntervals.IsRegionLevel(Place(1, "Сумська область", PlaceLevel.Region)));
        Assert.True(AlertIntervals.IsRegionLevel(Place(2, "Київ", PlaceLevel.City)));
        Assert.False(AlertIntervals.IsRegionLevel(Place(3, "Суми", PlaceLevel.City, 1)));
        Assert.False(AlertIntervals.IsRegionLevel(Place(4, "Громада", PlaceLevel.Hromada, 1)));
        Assert.False(AlertIntervals.IsRegionLevel(null));
    }
}
