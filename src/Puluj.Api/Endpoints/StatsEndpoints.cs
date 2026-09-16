using Puluj.Api.Services;

namespace Puluj.Api;

public static class StatsEndpoints
{
    /// <summary>
    /// Statistics page, one payload per tab: `/stats/targets`, `/stats/alerts`, `/stats/sources`, `/stats/recognition`.
    /// `from`/`to` optional (default: the last 24 h); the end is clamped to now, the span to StatsBuckets.MaxDays.
    /// Aggregates only — nothing about the viewer — so the answers are cached per section and period.
    /// </summary>
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/stats/targets", (DateTimeOffset? from, DateTimeOffset? to, StatsService stats, ReferenceCache refs, HttpContext http, CancellationToken ct) =>
            SectionAsync(from, to, http, async ct2 => { await refs.Ready.WaitAsync(ct2); return await stats.TargetsAsync(from, to, StatsFilter.From(http.Request.Query, refs), ct2); }, ct));
        api.MapGet("/stats/alerts", (DateTimeOffset? from, DateTimeOffset? to, StatsService stats, ReferenceCache refs, HttpContext http, CancellationToken ct) =>
            SectionAsync(from, to, http, async ct2 => { await refs.Ready.WaitAsync(ct2); return await stats.AlertsAsync(from, to, StatsFilter.From(http.Request.Query, refs), ct2); }, ct));
        api.MapGet("/stats/sources", (DateTimeOffset? from, DateTimeOffset? to, StatsService stats, ReferenceCache refs, HttpContext http, CancellationToken ct) =>
            SectionAsync(from, to, http, async ct2 => { await refs.Ready.WaitAsync(ct2); return await stats.SourcesAsync(from, to, StatsFilter.From(http.Request.Query, refs), ct2); }, ct));
        api.MapGet("/stats/recognition", (DateTimeOffset? from, DateTimeOffset? to, StatsService stats, ReferenceCache refs, HttpContext http, CancellationToken ct) =>
            SectionAsync(from, to, http, async ct2 => { await refs.Ready.WaitAsync(ct2); return await stats.RecognitionAsync(from, to, StatsFilter.From(http.Request.Query, refs), ct2); }, ct));
        return api;
    }

    private static async Task<IResult> SectionAsync<T>(DateTimeOffset? from, DateTimeOffset? to, HttpContext http, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (from is { } f && to is { } t && t <= f)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["`to` must be after `from`."] });
        }
        var dto = await load(ct);
        http.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Ok(dto);
    }
}
