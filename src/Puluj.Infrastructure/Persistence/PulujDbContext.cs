using Microsoft.EntityFrameworkCore;
using Puluj.Domain.Entities;
using Puluj.Domain.Entities.Messaging;
using Puluj.Domain.Entities.Processing;

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
    public DbSet<EventKindRuleShadow> EventKindRuleShadows => Set<EventKindRuleShadow>();

    public DbSet<Place> Places => Set<Place>();

    public DbSet<Target> Targets => Set<Target>();
    public DbSet<TargetTrack> TargetTracks => Set<TargetTrack>();
    public DbSet<TrackTarget> TrackTargets => Set<TrackTarget>();
    public DbSet<TargetLink> TargetLinks => Set<TargetLink>();
    public DbSet<SourceCopy> SourceCopies => Set<SourceCopy>();
    public DbSet<SourceDailyStat> SourceDailyStats => Set<SourceDailyStat>();
    public DbSet<TargetTrackRevision> TargetTrackRevisions => Set<TargetTrackRevision>();

    public DbSet<AirAlert> AirAlerts => Set<AirAlert>();
    public DbSet<CollectorState> CollectorStates => Set<CollectorState>();
    public DbSet<ProcessingError> ProcessingErrors => Set<ProcessingError>();
    public DbSet<UserLocation> UserLocations => Set<UserLocation>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    // Message platform (P03, ADR-0006): schemas `messaging` and `processing`; written with plain SQL in the same
    // transaction as the business result, read through EF for reconciliation, admin and tests.
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxEntry> Inbox => Set<InboxEntry>();
    public DbSet<ArchivedEvent> ArchivedEvents => Set<ArchivedEvent>();
    public DbSet<EventLink> EventLinks => Set<EventLink>();
    public DbSet<SubscriptionRegistration> Subscriptions => Set<SubscriptionRegistration>();
    public DbSet<TopologyVersionRecord> TopologyVersions => Set<TopologyVersionRecord>();
    public DbSet<ProcessingRun> ProcessingRuns => Set<ProcessingRun>();
    public DbSet<ProcessingGeneration> ProcessingGenerations => Set<ProcessingGeneration>();
    public DbSet<StageResult> StageResults => Set<StageResult>();
    public DbSet<ProcessingAttempt> ProcessingAttempts => Set<ProcessingAttempt>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<QuarantineEntry> Quarantine => Set<QuarantineEntry>();
    public DbSet<Extraction> Extractions => Set<Extraction>();
    public DbSet<Observation> Observations => Set<Observation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PulujDbContext).Assembly);
    }
}
