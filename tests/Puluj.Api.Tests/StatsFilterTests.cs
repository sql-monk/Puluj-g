using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Api.Services;

namespace Puluj.Api.Tests;

public sealed class StatsFilterTests
{
    private static StatsFilter Filter(string query)
    {
        // These tests only exercise scalar filters, so an unstarted reference cache is sufficient (its dictionaries
        // are deliberately empty until refresh). It prevents a database fixture from hiding predicate regressions.
        var refs = new ReferenceCache(null!, NullLogger<ReferenceCache>.Instance);
        return StatsFilter.From(new QueryCollection(QueryHelpers.ParseQuery(query)), refs);
    }

    [Fact]
    public void TargetRawAndTrackPredicatesShareCanonicalDuplicateRule()
    {
        var filter = Filter("?sourceIds=9,7&categoryIds=3");
        var target = filter.TargetPredicate("t", true);
        var raw = filter.RawPredicate("r");
        var tracks = filter.TrackPredicate("tt");

        Assert.Contains("t.duplicate_of_target_id IS NULL", target);
        Assert.Contains("t.duplicate_of_target_id IS NULL", raw);
        Assert.Contains("t.duplicate_of_target_id IS NULL", tracks);
        Assert.Contains("r.source_id IN (7,9)", raw);
        Assert.Contains("t.source_id IN (7,9)", tracks);
        Assert.Contains("t.target_category_id IN (3)", raw);
    }

    [Fact]
    public void CanonicalCacheKeyIsIndependentOfUrlListOrder()
    {
        var a = Filter("?sourceIds=9,7&categoryIds=3,2");
        var b = Filter("?categoryIds=2,3&sourceIds=7,9");
        Assert.Equal(a.CacheKey, b.CacheKey);
        Assert.Equal("categoryIds=2,3&sourceIds=7,9", a.CacheKey);
    }

    [Fact]
    public void UnsupportedAlertDimensionIsExplicitInsteadOfAppliedSilently()
    {
        var meta = Filter("?categoryIds=3&sourceIds=7").Meta("alerts");
        Assert.Equal(["sourceIds=7"], meta.Applied);
        var unavailable = Assert.Single(meta.Unavailable);
        Assert.Equal("categoryIds", unavailable.Key);
        Assert.Contains("факти", unavailable.Reason);
    }
}
