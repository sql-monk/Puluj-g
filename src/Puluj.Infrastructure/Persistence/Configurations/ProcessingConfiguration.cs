using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities.Processing;

namespace Puluj.Infrastructure.Persistence.Configurations;

/// <summary>Schema `processing` (ADR-0005/0006, P03). Additive; `raw_messages.processing_status` stays a compatibility projection.</summary>
public class ProcessingRunConfiguration : IEntityTypeConfiguration<ProcessingRun>
{
    public void Configure(EntityTypeBuilder<ProcessingRun> b)
    {
        b.ToTable("runs", "processing");
        b.HasKey(x => x.RunId);
        b.Property(x => x.RunId).ValueGeneratedNever();
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.Kind).HasMaxLength(16);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.CreatedBy).HasMaxLength(128);
        b.Property(x => x.Versions).HasColumnType("jsonb");
        b.Property(x => x.Scope).HasColumnType("jsonb");
        b.Property(x => x.Checkpoint).HasColumnType("jsonb");
        b.HasIndex(x => new { x.Lane, x.State });
        // P03: one open run per lane (live/history); replay runs (P14) are many and never `running` on lane live.
        b.HasIndex(x => x.Lane, "ux_processing_runs_open_per_lane").HasDatabaseName("ux_processing_runs_open_per_lane").IsUnique().HasFilter("state = 'running' AND kind IN ('live', 'history')");
    }
}

public class ProcessingGenerationConfiguration : IEntityTypeConfiguration<ProcessingGeneration>
{
    public void Configure(EntityTypeBuilder<ProcessingGeneration> b)
    {
        b.ToTable("generations", "processing");
        b.HasKey(x => x.GenerationId);
        b.Property(x => x.GenerationId).ValueGeneratedNever();
        b.Property(x => x.VerifiedBy).HasMaxLength(128);
        b.HasIndex(x => x.IsActive, "ux_processing_generations_active").HasDatabaseName("ux_processing_generations_active").IsUnique().HasFilter("is_active");
    }
}

public class StageResultConfiguration : IEntityTypeConfiguration<StageResult>
{
    public void Configure(EntityTypeBuilder<StageResult> b)
    {
        b.ToTable("stage_results", "processing");
        b.HasKey(x => x.StageResultId);
        b.Property(x => x.Stage).HasMaxLength(32);
        b.Property(x => x.StageVersion).HasMaxLength(64);
        b.Property(x => x.Outcome).HasMaxLength(32);
        b.Property(x => x.Worker).HasMaxLength(128);
        b.Property(x => x.Outputs).HasColumnType("jsonb");
        b.Property(x => x.Versions).HasColumnType("jsonb");
        b.HasIndex(x => new { x.RawMessageId, x.RunId, x.Stage, x.StageVersion }).IsUnique();
        b.HasIndex(x => new { x.RunId, x.Stage, x.Outcome });
    }
}

public class ProcessingAttemptConfiguration : IEntityTypeConfiguration<ProcessingAttempt>
{
    public void Configure(EntityTypeBuilder<ProcessingAttempt> b)
    {
        b.ToTable("attempts", "processing");
        b.HasKey(x => x.AttemptId);
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.JobKey).HasMaxLength(160);
        b.Property(x => x.Worker).HasMaxLength(128);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.Error).HasMaxLength(4096);
        b.Property(x => x.RetryReason).HasMaxLength(64);
        b.HasIndex(x => new { x.SubscriptionId, x.EventId });
        b.HasIndex(x => new { x.JobKey, x.FencingToken });
        b.HasIndex(x => x.StageResultId);
        b.HasIndex(x => x.StartedAt).HasMethod("brin");
    }
}

public class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> b)
    {
        b.ToTable("deliveries", "processing");
        b.HasKey(x => new { x.EventId, x.SubscriptionId });
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Outcome).HasMaxLength(16);
        b.Property(x => x.Reason).HasMaxLength(512);
        b.Property(x => x.Actor).HasMaxLength(128);
        // Reconciliation: expected rows without a terminal receipt, oldest first.
        b.HasIndex(x => x.ExpectedAt, "ix_processing_deliveries_pending").HasDatabaseName("ix_processing_deliveries_pending").HasFilter("outcome IS NULL");
        b.HasIndex(x => new { x.SubscriptionId, x.Outcome });
    }
}

public class QuarantineEntryConfiguration : IEntityTypeConfiguration<QuarantineEntry>
{
    public void Configure(EntityTypeBuilder<QuarantineEntry> b)
    {
        b.ToTable("quarantine", "processing");
        b.HasKey(x => x.QuarantineId);
        b.Property(x => x.SubscriptionId).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.Reason).HasMaxLength(64);
        b.Property(x => x.Error).HasMaxLength(4096);
        b.Property(x => x.ResolvedBy).HasMaxLength(128);
        b.Property(x => x.Resolution).HasMaxLength(16);
        b.Property(x => x.Envelope).HasColumnType("jsonb");
        b.Property(x => x.Headers).HasColumnType("jsonb");
        b.HasIndex(x => new { x.SubscriptionId, x.EventId });
        // At most one open quarantine per delivery; a retry resolves it and a later failure opens a new one.
        b.HasIndex(x => new { x.SubscriptionId, x.EventId }, "ux_processing_quarantine_open").HasDatabaseName("ux_processing_quarantine_open").IsUnique().HasFilter("resolved_at IS NULL");
    }
}
