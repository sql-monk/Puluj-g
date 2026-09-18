using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Api.Hubs;
using Puluj.Api.Services;
using Puluj.Contracts;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;

namespace Puluj.Api;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapPulujEndpoints(this IEndpointRouteBuilder app, bool isDevelopment)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/version", () => new { version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0" });

        // The live map's time windows: the client's lifetime picker and its own pruning use the server's numbers.
        api.MapGet("/map/config", (IOptions<MapOptions> map, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return new MapConfigDto(map.Value.LifetimeOptionsMinutes, (int)map.Value.MaxLifetime.TotalMinutes, map.Value.FeedHours);
        });

        // Live state or the state at a moment in the past (spec §20). Same shape for both.
        api.MapGet("/snapshot", async (HttpContext http, DateTimeOffset? at, bool? activeOnly, SnapshotService snapshots, CancellationToken ct) =>
        {
            try { return Results.Json(at is null ? await snapshots.LiveAsync(activeOnly ?? true, ct, MapFilter.From(http.Request.Query)) : await snapshots.AtAsync(at.Value, activeOnly ?? true, ct, MapFilter.From(http.Request.Query)), ApiDependencyInjection.MapJsonOptions()); }
            catch (MapFutureHistoryException ex) { return Results.BadRequest(new { code = "future_history", detail = ex.Message }); }
        });

        // Replay window (spec §20): every track that was reported inside it, with all its reported positions, so the
        // client can animate the movement between reports instead of asking for a snapshot per tick.
        api.MapGet("/replay", async (HttpContext http, DateTimeOffset from, DateTimeOffset to, SnapshotService snapshots, CancellationToken ct) =>
        {
            try { return Results.Json(await snapshots.ReplayAsync(from, to, ct, MapFilter.From(http.Request.Query)), ApiDependencyInjection.MapJsonOptions()); }
            catch (MapWindowTooLargeException ex) { return Results.BadRequest(new { code = "window_too_large", maximumHours = ex.Maximum.TotalHours, detail = ex.Message }); }
            catch (MapFutureHistoryException ex) { return Results.BadRequest(new { code = "future_history", detail = ex.Message }); }
        });

        // The probable predecessors of the track's newest target (all of them, two generations back by default).
        api.MapGet("/tracks/{id:long}/predecessors", async (long id, int? depth, SnapshotService snapshots, CancellationToken ct) =>
            await snapshots.PredecessorsAsync(id, Math.Clamp(depth ?? 2, 1, 4), ct) is { } p ? Results.Json(p, ApiDependencyInjection.MapJsonOptions()) : Results.NotFound());

        api.MapGet("/tracks/{id:long}", async (long id, SnapshotService snapshots, CancellationToken ct) =>
            await snapshots.TrackDetailsAsync(id, ct) is { } details ? Results.Json(details, ApiDependencyInjection.MapJsonOptions()) : Results.NotFound());

        // One report with its message, source and kinematic links (the link window between two reports).
        api.MapGet("/targets/{id:long}", async (long id, SnapshotService snapshots, CancellationToken ct) =>
            await snapshots.TargetAsync(id, ct) is { } o ? Results.Json(o, ApiDependencyInjection.MapJsonOptions()) : Results.NotFound());

        // Feed: newest targets for the side panel (the feed window by default, never further back). `until` bounds a replay window.
        api.MapGet("/targets", async (DateTimeOffset? since, DateTimeOffset? until, int? limit, SnapshotService snapshots, TimeProvider clock, IOptions<MapOptions> map, CancellationToken ct) =>
            Results.Json(await snapshots.RecentTargetsAsync(since ?? clock.GetUtcNow() - map.Value.FeedWindow, until, limit ?? 300, ct), ApiDependencyInjection.MapJsonOptions()));

        // Alert history of one place — on it, covering it or inside it (the region window: current alert, last one, count and total time over 24 h).
        api.MapGet("/alerts/history", async (int placeId, double? hours, SnapshotService snapshots, CancellationToken ct) =>
            Results.Json(await snapshots.AlertHistoryAsync(placeId, hours ?? 24, ct), ApiDependencyInjection.MapJsonOptions()));

        api.MapGet("/timeline", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, int? bucketMinutes, SnapshotService snapshots, TimeProvider clock, CancellationToken ct) =>
        {
            var end = to ?? clock.GetUtcNow();
            var start = from ?? end.AddHours(-6);
            try { return Results.Ok(await snapshots.TimelineAsync(start, end, bucketMinutes ?? 15, ct, MapFilter.From(http.Request.Query))); }
            catch (MapWindowTooLargeException ex) { return Results.BadRequest(new { code = "window_too_large", maximumHours = ex.Maximum.TotalHours, detail = ex.Message }); }
            catch (MapFutureHistoryException ex) { return Results.BadRequest(new { code = "future_history", detail = ex.Message }); }
        });

        // U04 public catalogue: distinct from the legacy map endpoints.  PublicCatalogQueries is an allow-list adapter
        // over the existing read side; it never returns raw text/payload or parser metadata and its large collections page.
        api.MapGet("/public/entities", async (string? entityKinds, string? q, string? eventKinds, string? eventCategories, string? categoryIds, string? classIds, string? familyIds, string? modelIds,
            string? sourceIds, int? regionId, string? status, string? confidence, string? location, bool? hasResults, string? sort, DateTimeOffset? from, DateTimeOffset? to,
            string? cursor, int? pageSize, string? dataset, string? historyBasis, DateTimeOffset? at, PublicCatalogQueries catalogue, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await catalogue.ListAsync(new PublicCatalogQueries.Query(entityKinds, q, eventKinds, eventCategories, categoryIds, classIds, familyIds, modelIds,
                    sourceIds, regionId, status, confidence, location, hasResults, sort, from, to, cursor, pageSize, dataset, historyBasis, at), ct));
            }
            catch (PublicCatalogQueries.QueryException ex)
            {
                return Results.Problem(ex.Message, statusCode: ex.StatusCode);
            }
        });
        api.MapGet("/public/entities/{kind}/{id:long}", async (string kind, long id, string? dataset, string? historyBasis, DateTimeOffset? at, PublicCatalogQueries catalogue, CancellationToken ct) =>
        {
            try { return await catalogue.DetailsAsync(kind, id, dataset, historyBasis, at, ct) is { } details ? Results.Ok(details) : Results.NotFound(); }
            catch (PublicCatalogQueries.QueryException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        });
        api.MapGet("/public/entities/{kind}/{id:long}/evidence", async (string kind, long id, string? cursor, int? limit, string? dataset, PublicCatalogQueries catalogue, CancellationToken ct) =>
        {
            try { return await catalogue.EvidencePageAsync(kind, id, cursor, limit, dataset, ct) is { } page ? Results.Ok(page) : Results.NotFound(); }
            catch (PublicCatalogQueries.QueryException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        });
        api.MapGet("/public/entities/{kind}/{id:long}/messages", async (string kind, long id, string? cursor, int? limit, string? dataset, PublicCatalogQueries catalogue, CancellationToken ct) =>
        {
            try { return await catalogue.MessagePageAsync(kind, id, cursor, limit, dataset, ct) is { } page ? Results.Ok(page) : Results.NotFound(); }
            catch (PublicCatalogQueries.QueryException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        });
        api.MapGet("/public/entities/{kind}/{id:long}/relations", async (string kind, long id, string? cursor, int? limit, string? dataset, PublicCatalogQueries catalogue, CancellationToken ct) =>
        {
            try { return await catalogue.RelationPageAsync(kind, id, cursor, limit, dataset, ct) is { } page ? Results.Ok(page) : Results.NotFound(); }
            catch (PublicCatalogQueries.QueryException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        });

        // Historical disabled models remain available to a detail/catalogue client when explicitly asked; existing UI
        // continues to receive the enabled-only shape by default.
        api.MapGet("/taxonomy", (bool? includeDisabled, ReferenceCache refs) => includeDisabled == true ? refs.HistoricalTaxonomy : refs.Taxonomy);

        // Plan §8.2 catalog for the read-side; disabled kinds stay out of the UI (their targets keep the code in TargetDto).
        api.MapGet("/event-kinds", (bool? includeDisabled, ReferenceCache refs) =>
            refs.EventKinds.Values.Where(k => includeDisabled == true || k.Enabled).OrderBy(k => k.SortOrder).ThenBy(k => k.Code, StringComparer.Ordinal).Select(DtoMapper.EventKind));

        api.MapGet("/sources", (ReferenceCache refs, DtoMapper mapper) =>
            refs.Sources.Values.OrderByDescending(s => s.Priority).Select(mapper.Source));

        // Place search for "my location" (spec §16). Returns centroids only; nothing about the user is stored.
        api.MapGet("/places/search", (string q, int? limit, ReferenceCache refs, DtoMapper mapper) =>
        {
            var query = NameVariantGenerator.Normalize(q ?? "");
            if (query.Length < 2)
            {
                return Array.Empty<PlaceDto>();
            }
            return refs.Places.Values
                .Where(p => p.Level >= PlaceLevel.City || p.Level == PlaceLevel.Region)
                .Where(p => NameVariantGenerator.Normalize(p.Name).StartsWith(query, StringComparison.Ordinal))
                .OrderBy(p => p.Level == PlaceLevel.Region ? 1 : 0)
                .ThenByDescending(p => p.Population)
                .Take(Math.Clamp(limit ?? 10, 1, 50))
                .Select(mapper.Place)
                .ToArray();
        });

        api.MapGet("/places/{id:int}", (int id, ReferenceCache refs, DtoMapper mapper) =>
            refs.Place(id) is { } p ? Results.Ok(mapper.Place(p)) : Results.NotFound());

        // Polygons for regions/named areas: drawn under alerts and behind region-level markers. Cached by the client.
        api.MapGet("/places/regions", async (IDbContextFactory<PulujDbContext> factory, HttpContext http, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Places.AsNoTracking()
                // Oblasts, city-regions, raions (COD-AB) and Kyiv's city districts; hromada polygons are fetched one by one when an alert needs them.
                .Where(p => p.Level == PlaceLevel.Region || p.Level == PlaceLevel.NamedArea || p.Level == PlaceLevel.Country || (p.Level == PlaceLevel.City && p.ParentId == null)
                    || (p.Level == PlaceLevel.District && p.CountryCode == "UA"))
                .Select(p => new RegionDto(p.PlaceId, p.Name, p.Level.ToString(), p.CountryCode, p.ParentId, p.Geometry))
                .ToListAsync(ct);
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return rows;
        });

        api.MapGet("/places/{id:int}/geometry", async (int id, IDbContextFactory<PulujDbContext> factory, HttpContext http, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var geometry = await db.Places.AsNoTracking().Where(p => p.PlaceId == id).Select(p => p.Geometry).FirstOrDefaultAsync(ct);
            if (geometry is null)
            {
                return Results.NotFound();
            }
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Ok(geometry);
        });

        // Statistics page (charts over a period): src/Puluj.Api/Endpoints/StatsEndpoints.cs.
        api.MapStatsEndpoints();

        app.MapHub<MapHub>("/hubs/map");
        return app;
    }
}
