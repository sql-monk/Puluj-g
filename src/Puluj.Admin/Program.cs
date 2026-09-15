using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Puluj.Admin;
using Puluj.Admin.Docker;
using Puluj.Analytics;
using Puluj.Api;
using Puluj.Api.Services;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Settings;
using Serilog;

// The admin panel: settings, source rating, per-component status, logs. Runs as its own service on its own port
// with the read-write database role; the public map Api runs elsewhere with a read-only one.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPulujDatabaseSettings();

builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "puluj-admin"));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("puluj-admin"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMeter(PulujMetrics.MeterName).AddOtlpExporter());

builder.Services.AddPulujInfrastructure(builder.Configuration);
builder.Services.AddPulujAnalyticsReporting(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => ApiDependencyInjection.ConfigureJson(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("admin-test");
builder.Services.AddHttpClient("api-probe");
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddPolicy("dev", p => p.WithOrigins("http://localhost:5184", "http://127.0.0.1:5184").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<ReferenceCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReferenceCache>());
builder.Services.AddSingleton<DtoMapper>();
builder.Services.AddSingleton<SnapshotService>();
builder.Services.AddSingleton<LogReader>();
// P08 rule authoring: the parser and its indexes without the worker's background refresh (AdminIndexes refreshes on demand).
builder.Services.Configure<Puluj.Processing.Rules.RulesetOptions>(builder.Configuration.GetSection(Puluj.Processing.Rules.RulesetOptions.Section));
builder.Services.AddSingleton<Puluj.Processing.Indexes.IndexProvider>();
builder.Services.AddSingleton<Puluj.Processing.Indexes.IIndexes>(sp => sp.GetRequiredService<Puluj.Processing.Indexes.IndexProvider>());
builder.Services.AddSingleton<Puluj.Processing.Text.INormalizer, Puluj.Processing.Text.Normalizer>();
builder.Services.AddSingleton<Puluj.Processing.Parsing.RuleParser>();
builder.Services.AddSingleton<Puluj.Processing.Rules.RulesetEvaluator>();
builder.Services.AddSingleton<Puluj.Processing.Rules.RulesetPreview>();
builder.Services.AddSingleton<Puluj.Admin.Endpoints.AdminIndexes>();
builder.Services.AddSingleton<Puluj.Admin.Endpoints.KindCorpus>();
// Container management through the docker CLI and the mounted socket; off unless Docker__Enabled (the compose stack sets it).
builder.Services.AddOptions<DockerOptions>().Bind(builder.Configuration.GetSection(DockerOptions.Section));
builder.Services.AddSingleton<DockerService>();
builder.Services.AddHealthChecks()
    .AddNpgSql(sp => builder.Configuration.GetConnectionString(Puluj.Infrastructure.DependencyInjection.ConnectionStringName)!, name: "postgres", tags: ["db"]);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseCors("dev");
    app.MapOpenApi();
}

app.MapHealthChecks("/api/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync });
app.MapAdminEndpoints();
app.MapOpsEndpoints();
Puluj.Admin.Endpoints.RulesetEndpoints.MapRulesetEndpoints(app);
app.MapAnalyticsEndpoints();

// The admin SPA is built as admin.html (second Vite entry of the shared web/ code base).
app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = ["admin.html"] });
app.UseStaticFiles();
app.MapFallbackToFile("admin.html");

// Not a request is answered until the worker has brought the schema up to this code's model (SchemaReadiness).
await Puluj.Infrastructure.Persistence.SchemaReadiness.WaitForMigrationsAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
