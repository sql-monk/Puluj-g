namespace Puluj.EntityApi;

public static class EntityEndpoints
{
    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/ee");
        api.MapGet("/definitions", (EntityQueries queries, CancellationToken ct) => queries.DefinitionsAsync(ct));
        api.MapGet("/snapshot", (DateTimeOffset? at, EntityQueries queries, CancellationToken ct) => queries.SnapshotAsync(at, ct));
        api.MapGet("/entities", async (string? kind, string? kinds, string? q, string? sourceIds, DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, EntityQueries queries, CancellationToken ct) =>
        {
            if (!TryParseCursor(cursor, out var offset))
                return Results.BadRequest(new { error = $"cursor must be an integer from 0 to {EntityQueries.MaxCatalogueOffset}" });

            var page = await queries.CatalogueAsync(kind, kinds, q, sourceIds, from, to, Math.Clamp(limit ?? 100, 1, 500), offset, ct);
            return Results.Ok(page);
        });
        api.MapGet("/entities/{kind}/{id}", async (string kind, string id, EntityQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(kind, id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
        api.MapGet("/entities/{kind}/{id}/history", (string kind, string id, int? limit, EntityQueries queries, CancellationToken ct) =>
            queries.HistoryAsync(kind, id, Math.Clamp(limit ?? 100, 1, 500), ct));
        return app;
    }

    internal static bool TryParseCursor(string? cursor, out int offset)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            offset = 0;
            return true;
        }

        return int.TryParse(cursor, out offset) && offset >= 0 && offset <= EntityQueries.MaxCatalogueOffset;
    }
}
