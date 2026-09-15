using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;

namespace Puluj.Worker.Hosting;

/// <summary>
/// Applies migrations and runs seeders before collectors start. Uses a Postgres advisory lock so two workers never race.
/// With Worker:Roles=migrate alone the process stops right after (the compose `migrate` one-shot the other containers wait for).
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<PulujDbContext> factory,
    IEnumerable<ISeeder> seeders,
    TopologyRegistrar topology,
    IOptions<WorkerOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    private const long LockKey = 0x50554C554A; // "PULUJ"

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                // Migrations may backfill derived data (kinematic links, source statistics): far beyond the 30 s default.
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));
                await db.Database.MigrateAsync(ct);
            }

            foreach (var seeder in seeders.OrderBy(s => s.Order))
            {
                logger.LogInformation("Seeding: {Seeder}", seeder.GetType().Name);
                await seeder.SeedAsync(db, ct);
            }
            // Registry of expected subscriptions for this build's topology version (P03): collectors that start next
            // record expected deliveries from it, whether or not a broker role is running yet.
            await topology.EnsureRegisteredAsync(ct);
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", ct);
            await db.Database.CloseConnectionAsync();
        }
        if (options.Value.MigrateOnly)
        {
            logger.LogInformation("Database is up to date; migrate-only instance exiting");
            lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
