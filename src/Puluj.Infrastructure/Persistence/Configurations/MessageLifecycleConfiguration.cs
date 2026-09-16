using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities.Analytics;

namespace Puluj.Infrastructure.Persistence.Configurations;

/// <summary>
/// `analytics.message_lifecycle` (P15, ADR-0013): the lifecycle projection lives in the analytics schema but its DDL is owned by
/// these migrations — the `migrate` role runs before any consumer, so the `message-analytics` subscription never meets a missing
/// table (the Analytics worker maps the table without migrating it).
/// </summary>
public class MessageLifecycleConfiguration : IEntityTypeConfiguration<MessageLifecycle>
{
    public void Configure(EntityTypeBuilder<MessageLifecycle> b)
    {
        b.ToTable("message_lifecycle", "analytics");
        b.HasKey(x => new { x.RawMessageId, x.RunId });
        b.Property(x => x.SourceMessageKey).HasMaxLength(512);
        b.Property(x => x.SourceRevision).HasMaxLength(64);
        b.Property(x => x.Lane).HasMaxLength(16);
        b.Property(x => x.AnalysisOutcome).HasMaxLength(16);
        b.Property(x => x.Method).HasMaxLength(16);
        b.Property(x => x.Versions).HasColumnType("jsonb");
        b.Property(x => x.Timings).HasColumnType("jsonb");
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.SourceOfTruth).HasMaxLength(16);
        b.Property(x => x.LlmCostUsd).HasPrecision(12, 6);
        b.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_message_lifecycle_received_brin").HasMethod("brin");
        b.HasIndex(x => new { x.ReceivedAt, x.SourceId, x.AnalysisOutcome }).HasDatabaseName("ix_message_lifecycle_window");
        b.HasIndex(x => new { x.SourceId, x.SourceMessageKey }).HasDatabaseName("ix_message_lifecycle_post");
        // Reconciliation: rows still waiting for an analysis or a domain completion.
        b.HasIndex(x => new { x.ReceivedAt, x.RunId }).HasDatabaseName("ix_message_lifecycle_pending").HasFilter("analyzed_at IS NULL OR domain_completed_at IS NULL");
    }
}
