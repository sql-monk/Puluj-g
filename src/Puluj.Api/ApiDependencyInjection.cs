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

        services.AddSignalR().AddJsonProtocol(o => ConfigureMapJson(o.PayloadSerializerOptions));

        services.AddSingleton<ReferenceCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ReferenceCache>());
        services.AddSingleton<DtoMapper>();
        services.Configure<MapOptions>(configuration.GetSection(MapOptions.Section));
        services.AddSingleton<SnapshotService>();
        services.AddSingleton<IncidentQueries>();
        services.AddSingleton<PublicCatalogQueries>();
        services.AddSingleton<PublicMessageQueries>();
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

    /// <summary>Map payload identities stay exact in JavaScript; ordinary REST remains numerically compatible.</summary>
    public static void ConfigureMapJson(JsonSerializerOptions o)
    {
        ConfigureJson(o);
        o.Converters.Insert(0, new MapInt64Converter());
    }

    public static JsonSerializerOptions MapJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ConfigureMapJson(options);
        return options;
    }

    private sealed class MapInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String ? long.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : reader.GetInt64();
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

}
