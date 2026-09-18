using Puluj.Api;
using Puluj.EntityApi;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Settings;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPulujDatabaseSettings();
builder.Services.AddSerilog((sp, cfg) => cfg.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(sp).Enrich.FromLogContext().Enrich.WithProperty("app", "puluj-entity-api"));
builder.Services.AddPulujInfrastructure(builder.Configuration);
builder.Services.AddPulujApi(builder.Configuration);
builder.Services.AddCors(o => o.AddPolicy("entity-dev", p => p.WithOrigins("http://localhost:5193", "http://127.0.0.1:5193").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<EntityQueries>();
builder.Services.AddResponseCompression(o => o.MimeTypes = ["application/json", "application/geo+json", "text/plain"]);

var app = builder.Build();
app.UseResponseCompression();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseCors("entity-dev");
    app.MapOpenApi();
}
app.MapPulujEndpoints(app.Environment.IsDevelopment());
app.MapEntityEndpoints();
app.MapHealthChecks("/api/health");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await Puluj.Infrastructure.Persistence.SchemaReadiness.WaitForMigrationsAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
