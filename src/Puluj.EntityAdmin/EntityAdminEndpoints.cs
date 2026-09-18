using System.Net.Mime;

namespace Puluj.EntityAdmin;

public static class EntityAdminEndpoints
{
    public static IEndpointRouteBuilder MapEntityAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/admin/ee").AddEndpointFilter(AuthorizeAsync);
        api.MapGet("/overview", async (EntityAdminStore store, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var database = await store.OverviewAsync(ct);
            try
            {
                using var response = await clients.CreateClient("entity-extractor").GetAsync("health", ct);
                return Results.Ok(new { database, extractor = new { available = response.IsSuccessStatusCode, statusCode = (int)response.StatusCode } });
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new { database, extractor = new { available = false, error = error.Message } });
            }
        });
        api.MapGet("/settings", (EntityAdminStore store, CancellationToken ct) => store.SettingsAsync(ct));
        api.MapPut("/settings", async (SettingsRequest request, EntityAdminStore store, CancellationToken ct) => { await store.SaveSettingsAsync(request.Values, ct); return Results.Ok(new { saved = request.Values.Count }); });
        api.MapGet("/extractors", (EntityAdminStore store, CancellationToken ct) => store.ExtractorsAsync(ct));
        api.MapPut("/extractors", (ExtractorSaveRequest request, EntityAdminStore store, CancellationToken ct) => store.SaveExtractorAsync(request, ct));
        api.MapDelete("/extractors/{id:long}", (long id, EntityAdminStore store, CancellationToken ct) => store.DeleteExtractorAsync(id, ct));
        api.MapPost("/extractors/validate", (CodeRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken ct) => ProxyAsync(clients, configuration, "admin/validate", request, ct));
        api.MapPost("/extractors/test", (ExtractorTestRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken ct) => ProxyAsync(clients, configuration, "admin/test", request, ct));
        api.MapGet("/definitions", (EntityAdminStore store, CancellationToken ct) => store.DefinitionsAsync(ct));
        api.MapPost("/definitions", (EntityDefinitionSaveRequest request, EntityAdminStore store, CancellationToken ct) => store.CreateDefinitionAsync(request, ct));
        api.MapPut("/definitions/{id:long}/map", (long id, MapConfigRequest request, EntityAdminStore store, CancellationToken ct) => store.UpdateMapConfigAsync(id, request, ct));
        api.MapGet("/deliveries", (int? limit, EntityAdminStore store, CancellationToken ct) => store.DeliveriesAsync(Math.Clamp(limit ?? 100, 1, 500), ct));
        api.MapGet("/runs", (int? limit, EntityAdminStore store, CancellationToken ct) => store.RunsAsync(Math.Clamp(limit ?? 100, 1, 500), ct));
        api.MapPost("/enqueue/{rawMessageId:long}", (long rawMessageId, EntityAdminStore store, CancellationToken ct) => store.EnqueueAsync(rawMessageId, ct));
        return app;
    }

    private static async Task<IResult> ProxyAsync(IHttpClientFactory clients, IConfiguration configuration, string path, object body, CancellationToken ct)
    {
        var client = clients.CreateClient("entity-extractor");
        var token = configuration["EntityExtractor:AdminToken"];
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.TryAddWithoutValidation(Puluj.Admin.AdminEndpoints.TokenHeader, token);
        using var response = await client.PostAsJsonAsync(path, body, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(payload, response.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json, statusCode: (int)response.StatusCode);
    }

    internal static ValueTask<object?> AuthorizeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = configuration["Admin:Token"];
        if (!string.IsNullOrEmpty(expected))
        {
            var supplied = context.HttpContext.Request.Headers[Puluj.Admin.AdminEndpoints.TokenHeader].FirstOrDefault();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied ?? string.Empty), System.Text.Encoding.UTF8.GetBytes(expected)))
                return ValueTask.FromResult<object?>(Results.Unauthorized());
        }
        else if (context.HttpContext.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }
        return next(context);
    }
}

public sealed record CodeRequest(string Code);
public sealed record ExtractorTestRequest(string Code, object Message, int? TimeoutMs);
public sealed record ExtractorSaveRequest(long? ExtractorId, string Name, string Code, bool Enabled, int ExecutionOrder, int TimeoutMs);
public sealed record EntityFieldRequest(string Name, string Type, bool Required = false);
public sealed record EntityDefinitionSaveRequest(string EntityName, IReadOnlyList<EntityFieldRequest> Fields, MapConfigRequest Map, bool Enabled = true);
public sealed record MapConfigRequest(bool Enabled, string Renderer, string? LabelField, string? TimeField, string? StatusField, string? GeometryField, string? LatitudeField, string? LongitudeField, int? LifetimeMinutes, string? Svg, string? Color, double? Width, double? Opacity, string? Dash);
public sealed record SettingsRequest(IReadOnlyDictionary<string, string?> Values);
