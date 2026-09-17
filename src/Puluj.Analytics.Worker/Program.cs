using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Puluj.Analytics;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Reporting;
using Puluj.Analytics.Worker;
using Serilog;

// `--healthcheck`: probe the running instance and exit (the aspnet image has neither curl nor wget; compose runs this).
if (args.Contains("--healthcheck"))
{
    return await Healthcheck.RunAsync();
}

// The analytics service indexes raw messages for the independent track-first projection and serves its own status.
var builder = WebApplication.CreateBuilder(args);
var appName = $"puluj-{builder.Configuration[$"{AnalyticsOptions.Section}:Name"] ?? "analytics"}";

builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", appName));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(appName))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMeter(AnalyticsMetrics.MeterName).AddOtlpExporter());

builder.Services.AddPulujAnalytics(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddHostedService<AnalyticsInitializer>(); // first: the loop needs the schema
builder.Services.AddSingleton<AnalysisLoop>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisLoop>());
builder.Services.AddHostedService<AnalyticsHeartbeat>();

var app = builder.Build();
app.UseExceptionHandler();

// Healthy while the loop is alive and the last runs did not fail twice in a row.
app.MapGet("/health", (AnalysisLoop loop) =>
{
    var healthy = loop.ConsecutiveFailures < 2;
    var body = new { status = healthy ? "ok" : "failing", lastRunAt = loop.LastRunAt, consecutiveFailures = loop.ConsecutiveFailures, lastError = loop.LastError };
    return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/analytics/status", (AnalyticsReportService reports, CancellationToken ct) => reports.StatusAsync(ct));

app.Services.GetRequiredService<ILogger<Program>>().LogInformation("{App} starting", appName);
await app.RunAsync();
return 0;

public partial class Program;
