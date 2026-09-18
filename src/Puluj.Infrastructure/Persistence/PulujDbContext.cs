using Microsoft.EntityFrameworkCore;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence;

public class PulujDbContext(DbContextOptions<PulujDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<RawMessage> RawMessages => Set<RawMessage>();
    public DbSet<LlmRequest> LlmRequests => Set<LlmRequest>();

    public DbSet<TargetCategory> TargetCategories => Set<TargetCategory>();
    public DbSet<TargetClass> TargetClasses => Set<TargetClass>();
    public DbSet<TargetFamily> TargetFamilies => Set<TargetFamily>();
    public DbSet<TargetModel> TargetModels => Set<TargetModel>();
    public DbSet<TargetModelAlias> TargetModelAliases => Set<TargetModelAlias>();
    public DbSet<EventKind> EventKinds => Set<EventKind>();
    public DbSet<EventKindRuleset> EventKindRulesets => Set<EventKindRuleset>();
    public DbSet<EventKindRule> EventKindRules => Set<EventKindRule>();
    public DbSet<EventKindRulesetAudit> EventKindRulesetAudits => Set<EventKindRulesetAudit>();
    public DbSet<EventKindAudit> EventKindAudits => Set<EventKindAudit>();

    public DbSet<Place> Places => Set<Place>();

    public DbSet<Target> Targets => Set<Target>();
    public DbSet<TargetTrack> TargetTracks => Set<TargetTrack>();
    public DbSet<TrackTarget> TrackTargets => Set<TrackTarget>();
    public DbSet<TargetLink> TargetLinks => Set<TargetLink>();
    public DbSet<TargetTrackRevision> TargetTrackRevisions => Set<TargetTrackRevision>();

    public DbSet<AirAlert> AirAlerts => Set<AirAlert>();
    public DbSet<CollectorState> CollectorStates => Set<CollectorState>();
    public DbSet<ProcessingError> ProcessingErrors => Set<ProcessingError>();
    public DbSet<UserLocation> UserLocations => Set<UserLocation>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PulujDbContext).Assembly);
    }
}
