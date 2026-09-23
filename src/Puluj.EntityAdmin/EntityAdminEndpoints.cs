using System.Net.Mime;
using Puluj.Admin;
using Puluj.Domain.Entities;

namespace Puluj.EntityAdmin;

public static class EntityAdminEndpoints
{
    private static readonly HashSet<string> DeliveryStatuses = ["pending", "in_progress", "succeeded", "failed"];

    public static IEndpointRouteBuilder MapEntityAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/admin/ee").AddEndpointFilter(AuthorizeAsync);
        api.MapGet("/overview", async (EntityAdminStore store, IHttpClientFactory clients, Puluj.Infrastructure.Settings.SettingsStore settings, IConfiguration configuration, TimeProvider clock, CancellationToken ct) =>
        {
            var probe = await ProbeAsync(clients, ct);
            var queue = await store.QueueSnapshotAsync(ct);
            var counts = await store.CountsAsync(ct);
            var llmEnabled = LlmEnabled(await settings.GetAllAsync(ct), configuration);
            return Results.Ok(new EeOverviewDto(queue, counts.Extractors, counts.Definitions, counts.ActiveRuns, probe, llmEnabled,
                EntityExtractorStatus.Describe(probe, queue, llmEnabled, clock.GetUtcNow())));
        });
        api.MapGet("/settings", (EntityAdminStore store, CancellationToken ct) => store.SettingsAsync(ct));
        api.MapPut("/settings", async (SettingsRequest request, EntityAdminStore store, CancellationToken ct) =>
        {
            if (EntityExtractorSettings.Validate(request.Values) is { } problem) return Results.BadRequest(new { error = problem });
            await store.SaveSettingsAsync(request.Values, ct);
            return Results.Ok(new { saved = request.Values.Count });
        });
        api.MapGet("/extractors", (EntityAdminStore store, CancellationToken ct) => store.ExtractorsAsync(ct));
        api.MapPut("/extractors", (ExtractorSaveRequest request, EntityAdminStore store, CancellationToken ct) => store.SaveExtractorAsync(request, ct));
        api.MapDelete("/extractors/{id:long}", (long id, EntityAdminStore store, CancellationToken ct) => store.DeleteExtractorAsync(id, ct));
        api.MapPost("/extractors/validate", (CodeRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken ct) => ProxyAsync(clients, configuration, "admin/validate", request, ct));
        api.MapPost("/extractors/test", (ExtractorTestRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken ct) => ProxyAsync(clients, configuration, "admin/test", request, ct));
        api.MapGet("/definitions", (EntityAdminStore store, CancellationToken ct) => store.DefinitionsAsync(ct));
        api.MapPost("/definitions", (EntityDefinitionSaveRequest request, EntityAdminStore store, CancellationToken ct) => store.CreateDefinitionAsync(request, ct));
        api.MapPut("/definitions/{id:long}/map", (long id, MapConfigRequest request, EntityAdminStore store, CancellationToken ct) => store.UpdateMapConfigAsync(id, request, ct));
        api.MapGet("/deliveries", async (string? status, string? cursor, int? limit, EntityAdminStore store, CancellationToken ct) =>
        {
            var filter = string.IsNullOrEmpty(status) || status == "all" ? null : status;
            if (filter is not null && !DeliveryStatuses.Contains(filter)) return Results.BadRequest(new { error = "Невідомий статус доставки." });
            return Results.Ok(await store.DeliveriesAsync(filter, cursor, Math.Clamp(limit ?? 50, 1, 200), ct));
        });
        api.MapGet("/runs", async (long? beforeId, int? limit, EntityAdminStore store, CancellationToken ct) => Results.Ok(await store.RunsAsync(beforeId, Math.Clamp(limit ?? 50, 1, 200), ct)));
        api.MapPost("/enqueue/{rawMessageId:long}", (long rawMessageId, EntityAdminStore store, CancellationToken ct) => store.EnqueueAsync(rawMessageId, ct));
        return app;
    }

    /// <summary>The extractor's own health endpoint; a timeout or a refused connection is "unavailable", not an exception.</summary>
    internal static async Task<EeProbe> ProbeAsync(IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await clients.CreateClient("entity-extractor").GetAsync("health", timeout.Token);
            return new EeProbe(response.IsSuccessStatusCode, (int)response.StatusCode, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new EeProbe(false, null, error is TaskCanceledException ? "немає відповіді за 5 с" : error.Message);
        }
    }

    /// <summary>`Llm:Enabled` as the extractor reads it: app_settings first, then appsettings / environment.</summary>
    internal static bool LlmEnabled(IReadOnlyDictionary<string, AppSetting> settings, IConfiguration configuration)
    {
        var value = settings.TryGetValue("Llm:Enabled", out var stored) && !string.IsNullOrWhiteSpace(stored.Value) ? stored.Value : configuration["Llm:Enabled"];
        return bool.TryParse(value, out var enabled) && enabled;
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
public sealed record EeOverviewDto(EeQueueSnapshot Queue, long Extractors, long Definitions, long ActiveRuns, EeProbe Extractor, bool LlmEnabled, Puluj.Contracts.ServiceStatusDto Status);
