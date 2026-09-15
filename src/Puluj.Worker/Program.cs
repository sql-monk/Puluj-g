using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Puluj.Collectors;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Settings;
using Puluj.Messaging;
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
builder.Services.AddPulujInfrastructure(builder.Configuration, worker.InstanceName);
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
// Broker roles (P03): only with Messaging:Enabled — a plain local run has no RabbitMQ and must keep working.
var messaging = builder.Configuration.GetSection(MessagingOptions.Section).Get<MessagingOptions>() ?? new MessagingOptions();
var brokerRoles = roles.Intersect(WorkerOptions.BrokerRoles).ToHashSet();
if (brokerRoles.Count > 0 && messaging.Enabled)
{
    builder.Services.AddPulujMessaging(brokerRoles, worker.InstanceName);
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
var startupLog = host.Services.GetRequiredService<ILogger<Program>>();
startupLog.LogInformation("{App} starting with roles: {Roles}", appName, string.Join(", ", roles.Order()));
if (brokerRoles.Count > 0 && !messaging.Enabled && !string.IsNullOrEmpty(worker.Roles))
{
    startupLog.LogWarning("Roles {Roles} need Messaging:Enabled=true and a broker; skipped", string.Join(", ", brokerRoles.Order()));
}
if (messaging.Outbox.Enabled)
{
    startupLog.LogInformation("Outbox bridge enabled: raw.stored is committed to messaging.outbox; a `relay` role must run somewhere or the outbox only grows");
}
if (messaging.Ingress.Enabled)
{
    // The collectors of this process publish ingress.received; nothing reaches raw_messages until `relay` and `raw-writer`
    // roles run (here or elsewhere). Reconciliation alarms on outbox age / overdue deliveries if they do not.
    var hasRawWriter = roles.Contains(WorkerOptions.RawWriter) && roles.Contains(WorkerOptions.Relay) && messaging.Enabled;
    startupLog.Log(hasRawWriter ? LogLevel.Information : LogLevel.Warning,
        "Ingress enabled: collectors commit ingress.received + checkpoint to messaging.outbox; raw rows are written by the raw-writer subscription{Where}",
        hasRawWriter ? " (running in this process)" : " — make sure `relay` and `raw-writer` roles run with Messaging:Enabled in another process");
}
await host.RunAsync();
