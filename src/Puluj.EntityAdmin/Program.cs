using Puluj.Admin;
using Puluj.EntityAdmin.Docker;
using Puluj.Analytics;
using Puluj.Api;
using Puluj.Api.Services;
using Puluj.EntityAdmin;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Settings;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPulujDatabaseSettings();
builder.Services.AddSerilog((sp, cfg) => cfg.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(sp).Enrich.FromLogContext().Enrich.WithProperty("app", "puluj-entity-admin"));
builder.Services.AddPulujInfrastructure(builder.Configuration);
builder.Services.AddPulujAnalyticsReporting(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => ApiDependencyInjection.ConfigureJson(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("admin-test");
builder.Services.AddHttpClient("api-probe");
builder.Services.AddHttpClient("entity-extractor", (sp, client) =>
{
    var url = sp.GetRequiredService<IConfiguration>()["EntityExtractor:Url"] ?? "http://localhost:8100";
    client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddPolicy("dev", p => p.WithOrigins("http://localhost:5194", "http://127.0.0.1:5194").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<ReferenceCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReferenceCache>());
builder.Services.AddSingleton<DtoMapper>();
builder.Services.AddSingleton<SnapshotService>();
builder.Services.AddSingleton<Puluj.Admin.LogReader>();
builder.Services.Configure<Puluj.Processing.Rules.RulesetOptions>(builder.Configuration.GetSection(Puluj.Processing.Rules.RulesetOptions.Section));
builder.Services.AddSingleton<Puluj.Processing.Indexes.IndexProvider>();
builder.Services.AddSingleton<Puluj.Processing.Indexes.IIndexes>(sp => sp.GetRequiredService<Puluj.Processing.Indexes.IndexProvider>());
builder.Services.AddSingleton<Puluj.Processing.Text.INormalizer, Puluj.Processing.Text.Normalizer>();
builder.Services.AddSingleton<Puluj.Processing.Parsing.RuleParser>();
builder.Services.AddSingleton<Puluj.Processing.Rules.RulesetEvaluator>();
builder.Services.AddSingleton<Puluj.Processing.Rules.RulesetPreview>();
builder.Services.AddSingleton<Puluj.Admin.Endpoints.AdminIndexes>();
builder.Services.AddSingleton<Puluj.Admin.Endpoints.KindCorpus>();
builder.Services.AddOptions<DockerOptions>().Bind(builder.Configuration.GetSection(DockerOptions.Section));
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<EntityAdminStore>();
builder.Services.AddHealthChecks().AddNpgSql(sp => builder.Configuration.GetConnectionString(Puluj.Infrastructure.DependencyInjection.ConnectionStringName)!, name: "postgres", tags: ["db"]);

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) { app.UseCors("dev"); app.MapOpenApi(); }
app.MapHealthChecks("/api/health");
app.MapAdminEndpoints();
app.MapEntityOpsEndpoints();
Puluj.Admin.Endpoints.RulesetEndpoints.MapRulesetEndpoints(app);
Puluj.Admin.Endpoints.CatalogEndpoints.MapCatalogEndpoints(app);
app.MapAnalyticsEndpoints();
app.MapEntityAdminEndpoints();
app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = ["admin.html"] });
app.UseStaticFiles();
app.MapFallbackToFile("admin.html");
await Puluj.Infrastructure.Persistence.SchemaReadiness.WaitForMigrationsAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
