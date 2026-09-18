using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence.Configurations;

public sealed class EntityDeliveryConfiguration : IEntityTypeConfiguration<EntityDelivery>
{
    public void Configure(EntityTypeBuilder<EntityDelivery> b)
    {
        b.ToTable("ee_delivery_queue");
        b.HasKey(x => x.DeliveryId);
        b.Property(x => x.DeliveryId).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.Origin).HasMaxLength(16);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.ClaimedBy).HasMaxLength(128);
        b.HasIndex(x => new { x.Status, x.EnqueuedAt });
        b.HasIndex(x => x.LeaseExpiresAt).HasFilter("status = 'in_progress'");
        b.HasIndex(x => x.RawMessageId, "ux_ee_delivery_queue_live_raw_message")
            .IsUnique().HasFilter("origin = 'live'");
        b.HasOne(x => x.RawMessage).WithMany().HasForeignKey(x => x.RawMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EntityDeliveryAttemptConfiguration : IEntityTypeConfiguration<EntityDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<EntityDeliveryAttempt> b)
    {
        b.ToTable("ee_delivery_attempts");
        b.HasKey(x => x.DeliveryAttemptId);
        b.Property(x => x.Outcome).HasMaxLength(32);
        b.HasIndex(x => new { x.DeliveryId, x.StartedAt });
        b.HasOne(x => x.Delivery).WithMany(x => x.DeliveryAttempts).HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EntityExtractorDefinitionConfiguration : IEntityTypeConfiguration<EntityExtractorDefinition>
{
    public void Configure(EntityTypeBuilder<EntityExtractorDefinition> b)
    {
        b.ToTable("ee_extractors");
        b.HasKey(x => x.ExtractorId);
        b.Property(x => x.Name).HasMaxLength(128);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => new { x.Enabled, x.ExecutionOrder });
    }
}

public sealed class EntityDefinitionConfiguration : IEntityTypeConfiguration<EntityDefinition>
{
    public void Configure(EntityTypeBuilder<EntityDefinition> b)
    {
        b.ToTable("ee_entity_definitions");
        b.HasKey(x => x.EntityDefinitionId);
        b.Property(x => x.EntityName).HasMaxLength(63);
        b.Property(x => x.TableName).HasMaxLength(63);
        b.Property(x => x.Fields).HasColumnType("jsonb");
        b.Property(x => x.MapSettings).HasColumnType("jsonb");
        b.HasIndex(x => x.EntityName).IsUnique();
        b.HasIndex(x => x.TableName).IsUnique();
    }
}

public sealed class EntityProcessingRunConfiguration : IEntityTypeConfiguration<EntityProcessingRun>
{
    public void Configure(EntityTypeBuilder<EntityProcessingRun> b)
    {
        b.ToTable("ee_processing_runs");
        b.HasKey(x => x.ProcessingRunId);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.ClaimToken).HasDefaultValueSql("gen_random_uuid()");
        b.HasIndex(x => x.DeliveryId).IsUnique();
        b.HasIndex(x => new { x.RawMessageId, x.StartedAt });
        b.HasOne(x => x.Delivery).WithOne().HasForeignKey<EntityProcessingRun>(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.RawMessage).WithMany().HasForeignKey(x => x.RawMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EntityExtractorRunConfiguration : IEntityTypeConfiguration<EntityExtractorRun>
{
    public void Configure(EntityTypeBuilder<EntityExtractorRun> b)
    {
        b.ToTable("ee_extractor_runs");
        b.HasKey(x => x.ExtractorRunId);
        b.Property(x => x.Status).HasMaxLength(32);
        b.HasIndex(x => new { x.ProcessingRunId, x.ExtractorId }).IsUnique();
        b.HasOne(x => x.ProcessingRun).WithMany(x => x.ExtractorRuns).HasForeignKey(x => x.ProcessingRunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Extractor).WithMany().HasForeignKey(x => x.ExtractorId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EntityWriteAuditConfiguration : IEntityTypeConfiguration<EntityWriteAudit>
{
    public void Configure(EntityTypeBuilder<EntityWriteAudit> b)
    {
        b.ToTable("ee_entity_writes");
        b.HasKey(x => x.EntityWriteId);
        b.Property(x => x.TableName).HasMaxLength(63);
        b.HasIndex(x => new { x.ProcessingRunId, x.CreatedAt });
        b.HasOne(x => x.ProcessingRun).WithMany().HasForeignKey(x => x.ProcessingRunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ExtractorRun).WithMany(x => x.EntityWrites).HasForeignKey(x => x.ExtractorRunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.EntityDefinition).WithMany().HasForeignKey(x => x.EntityDefinitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
