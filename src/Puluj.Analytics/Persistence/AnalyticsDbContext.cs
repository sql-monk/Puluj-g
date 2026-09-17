using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Puluj.Analytics.Persistence;

/// <summary>
/// The `analytics` schema: its own migrations history (`analytics.__EFMigrationsHistory`), so the pipeline's
/// `PulujDbContext` and this one never see each other's migrations. The public tables (`raw_messages`, `sources`,
/// `targets`…) are read through plain SQL on the same connection, never mapped here.
/// </summary>
public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public const string Schema = "analytics";

    public DbSet<AnalyticsState> State => Set<AnalyticsState>();
    public DbSet<AnalysisRun> Runs => Set<AnalysisRun>();
    public DbSet<MessageFingerprint> Messages => Set<MessageFingerprint>();
    public DbSet<TrackFirst> TrackFirsts => Set<TrackFirst>();
    /// <summary>P15: the lifecycle projection (per raw message per run) — DDL owned by the pipeline's `PulujDbContext` migrations (schema `analytics`), mapped here read/write without migrations.</summary>
    public DbSet<Puluj.Domain.Entities.Analytics.MessageLifecycle> Lifecycle => Set<Puluj.Domain.Entities.Analytics.MessageLifecycle>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<AnalyticsState>(e =>
        {
            e.ToTable("state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
        });

        b.Entity<AnalysisRun>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.RunId);
            e.Property(x => x.Instance).HasMaxLength(64);
            e.HasIndex(x => x.StartedAt).IsDescending();
        });

        b.Entity<MessageFingerprint>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.RawMessageId);
            e.Property(x => x.RawMessageId).ValueGeneratedNever();
            e.Property(x => x.PostKey).HasMaxLength(256);
            e.HasIndex(x => x.PublishedAt);
            e.HasIndex(x => new { x.SourceId, x.PublishedAt });
        });

        b.Entity<TrackFirst>(e =>
        {
            e.ToTable("track_firsts");
            e.HasKey(x => new { x.Day, x.SourceId, x.TargetCategoryId });
            e.Property(x => x.CategoryCode).HasMaxLength(64);
        });

        b.Entity<Puluj.Domain.Entities.Analytics.MessageLifecycle>(e =>
        {
            e.ToTable("message_lifecycle", t => t.ExcludeFromMigrations()); // created by Puluj.Infrastructure migration AddMessageLifecycle (the `migrate` role runs before every consumer)
            e.HasKey(x => new { x.RawMessageId, x.RunId });
        });
    }

    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AnalyticsDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);
            })
            .UseSnakeCaseNamingConvention();
    }
}

/// <summary>Used by `dotnet ef` only (scripts/add-analytics-migration.ps1); no live database is needed.</summary>
public class AnalyticsDesignTimeFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Puluj")
            ?? "Host=localhost;Port=5442;Database=puluj;Username=puluj;Password=puluj";
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>();
        AnalyticsDbContext.Configure(options, cs);
        return new AnalyticsDbContext(options.Options);
    }
}
