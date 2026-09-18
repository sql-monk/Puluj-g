using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Puluj.Collectors;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Settings;
using Puluj.Processing;
using Puluj.Worker.Hosting;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPulujDatabaseSettings(); // values from the admin UI override files/env

// Roles decide which hosted services this process runs (one image, several containers; see WorkerOptions).
var worker = builder.Configuration.GetSection(WorkerOptions.Section).Get<WorkerOptions>() ?? new WorkerOptions();
var roles = worker.RoleSet;
var appName = $"puluj-{worker.InstanceName}";

builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", appName));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(appName))
    .WithTracing(t => t.AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(PulujMetrics.MeterName).AddOtlpExporter());

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
builder.Services.AddPulujInfrastructure(builder.Configuration);
if (roles.Contains(WorkerOptions.Migrate))
{
    builder.Services.AddHostedService<DatabaseInitializer>(); // first: the others need the schema and the seed data
}
if (!worker.MigrateOnly)
{
    builder.Services.AddHostedService<WorkerHeartbeat>();
    builder.Services.AddHostedService<WorkerStatusReporter>();
}
if (roles.Contains(WorkerOptions.Processing))
{
    builder.Services.AddPulujProcessing(builder.Configuration, worker.InstanceName);
}
var collectors = new List<string>();
if (roles.Contains(WorkerOptions.Telegram))
{
    collectors.Add(CollectorNames.Telegram);
}
if (roles.Contains(WorkerOptions.Alerts))
{
    collectors.Add(CollectorNames.AlertsInUa);
}
if (collectors.Count > 0)
{
    builder.Services.AddPulujCollectors(builder.Configuration, collectors);
}

var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>().LogInformation("{App} starting with roles: {Roles}", appName, string.Join(", ", roles.Order()));
await host.RunAsync();
