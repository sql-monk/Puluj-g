using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetTopologySuite.IO.Converters;
using Puluj.Api.Health;
using Puluj.Api.Services;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api;

public static class ApiDependencyInjection
{
    public static IServiceCollection AddPulujApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));
        services.AddProblemDetails();
        services.AddHttpClient("admin-test");
        services.AddOpenApi();
        services.AddCors(o => o.AddPolicy("dev", p => p
            .WithOrigins("http://localhost:5183")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        services.AddSignalR().AddJsonProtocol(o => ConfigureJson(o.PayloadSerializerOptions));

        services.AddSingleton<ReferenceCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ReferenceCache>());
        services.AddSingleton<DtoMapper>();
        services.Configure<MapOptions>(configuration.GetSection(MapOptions.Section));
        services.AddSingleton<SnapshotService>();
        services.AddSingleton<IncidentQueries>();
        services.AddSingleton<PublicCatalogQueries>();
        // Statistics page: aggregates cached per period (every entry Size = 1, at most 64 periods in memory).
        services.AddMemoryCache(o => o.SizeLimit = 64);
        services.AddSingleton<StatsService>();
        services.AddHostedService<NotifyBridge>();

        services.AddHealthChecks()
            .AddNpgSql(sp => configuration.GetConnectionString(Infrastructure.DependencyInjection.ConnectionStringName)!, name: "postgres", tags: ["db"])
            .AddCheck<CollectorsHealthCheck>("collectors", tags: ["collectors"]);

        return services;
    }

    public static void ConfigureJson(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new GeoJsonConverterFactory(Geo.Factory));
    }
}
