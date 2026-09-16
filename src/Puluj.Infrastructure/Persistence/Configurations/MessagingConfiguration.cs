using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities.Messaging;

namespace Puluj.Infrastructure.Persistence.Configurations;

/// <summary>Schema `messaging` (ADR-0006, P03). Additive: nothing outside the schema is touched.</summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox", "messaging");
        b.HasKey(x => x.OutboxId);
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.RoutingKey).HasMaxLength(128);
        b.Property(x => x.TargetQueue).HasMaxLength(128);
        b.Property(x => x.LeaseOwner).HasMaxLength(128);
        b.Property(x => x.LastError).HasMaxLength(2048);
        b.Property(x => x.Envelope).HasColumnType("jsonb");
        // One fan-out publication per event; redelivery rows (target_queue set) may repeat the event id.
        b.HasIndex(x => x.EventId, "ix_messaging_outbox_event_id_fanout").HasDatabaseName("ix_messaging_outbox_event_id_fanout").IsUnique().HasFilter("target_queue IS NULL");
        // Relay scan: unconfirmed rows in creation order; stays small because confirmed rows leave the index.
        b.HasIndex(x => x.NextAttemptAt, "ix_messaging_outbox_unconfirmed").HasDatabaseName("ix_messaging_outbox_unconfirmed").HasFilter("confirmed_at IS NULL");
        // Cleanup: confirmed rows older than the grace period.
        b.HasIndex(x => x.ConfirmedAt, "ix_messaging_outbox_confirmed_at").HasDatabaseName("ix_messaging_outbox_confirmed_at").HasFilter("confirmed_at IS NOT NULL");
    }
}

public class InboxEntryConfiguration : IEntityTypeConfiguration<InboxEntry>
{
    public void Configure(EntityTypeBuilder<InboxEntry> b)
    {
        b.ToTable("inbox", "messaging");
        b.HasKey(x => new { x.SubscriptionId, x.EventId });
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Outcome).HasMaxLength(16);
        b.HasIndex(x => x.CompletedAt).HasMethod("brin"); // retention sweep
    }
}

public class SubscriptionLaneConfiguration : IEntityTypeConfiguration<SubscriptionLane>
{
    public void Configure(EntityTypeBuilder<SubscriptionLane> b)
    {
        b.ToTable("subscription_lanes", "messaging");
        b.HasKey(x => new { x.SubscriptionId, x.Lane });
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Actor).HasMaxLength(128);
        b.ToTable(t => t.HasCheckConstraint("ck_subscription_lanes_state", "state IN ('active', 'paused', 'draining')"));
    }
}

public class ControlAuditConfiguration : IEntityTypeConfiguration<ControlAudit>
{
    public void Configure(EntityTypeBuilder<ControlAudit> b)
    {
        b.ToTable("control_audit", "messaging");
        b.HasKey(x => x.AuditId);
        b.Property(x => x.Action).HasMaxLength(32);
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.Actor).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasIndex(x => x.At).IsDescending();
        b.HasIndex(x => new { x.SubscriptionId, x.Lane, x.At }).IsDescending(false, false, true);
    }
}

public class ArchivedEventConfiguration : IEntityTypeConfiguration<ArchivedEvent>
{
    public void Configure(EntityTypeBuilder<ArchivedEvent> b)
    {
        b.ToTable("events", "messaging");
        b.HasKey(x => x.EventId);
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.Envelope).HasColumnType("jsonb");
        b.HasIndex(x => x.CorrelationId);
        b.HasIndex(x => new { x.RawMessageId, x.ProcessingRunId });
        b.HasIndex(x => x.ProcessingRunId).HasDatabaseName("ix_messaging_events_run"); // P14 verify: every delivery of a replay run
        b.HasIndex(x => new { x.EventType, x.PublishedAt });
        b.HasIndex(x => x.PublishedAt).HasMethod("brin");
    }
}

public class EventLinkConfiguration : IEntityTypeConfiguration<EventLink>
{
    public void Configure(EntityTypeBuilder<EventLink> b)
    {
        b.ToTable("event_links", "messaging");
        b.HasKey(x => new { x.OutputEventId, x.InputEventId });
        b.HasIndex(x => x.InputEventId);
    }
}

public class SubscriptionRegistrationConfiguration : IEntityTypeConfiguration<SubscriptionRegistration>
{
    public void Configure(EntityTypeBuilder<SubscriptionRegistration> b)
    {
        b.ToTable("subscriptions", "messaging");
        b.HasKey(x => new { x.SubscriptionId, x.TopologyVersion });
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.QueuePolicy).HasMaxLength(32);
        b.Property(x => x.OwnerTask).HasMaxLength(16);
        b.Property(x => x.Bindings).HasColumnType("jsonb");
        b.Property(x => x.Lanes).HasColumnType("jsonb");
        b.Property(x => x.Waiver).HasColumnType("jsonb");
    }
}

public class TopologyVersionRecordConfiguration : IEntityTypeConfiguration<TopologyVersionRecord>
{
    public void Configure(EntityTypeBuilder<TopologyVersionRecord> b)
    {
        b.ToTable("topology_versions", "messaging");
        b.HasKey(x => x.TopologyVersion);
        b.Property(x => x.TopologyVersion).ValueGeneratedNever();
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.AppliedBy).HasMaxLength(128);
    }
}
