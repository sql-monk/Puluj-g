using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence.Configurations;

public class RawMessageConfiguration : IEntityTypeConfiguration<RawMessage>
{
    public void Configure(EntityTypeBuilder<RawMessage> b)
    {
        b.HasKey(x => x.RawMessageId);
        b.Property(x => x.SourceMessageId).HasMaxLength(256);
        b.Property(x => x.SourceMessageKey).HasMaxLength(512);
        b.Property(x => x.SourceRevision).HasMaxLength(64);
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.Url).HasMaxLength(2048);
        b.Property(x => x.RawPayload).HasColumnType("jsonb");
        b.Property(x => x.ClaimedBy).HasMaxLength(64);
        b.Property(x => x.ProcessingMs); // aggregated with percentile_cont over received_at/claimed_by ranges: no index of its own

        // Raw identity (ADR-0003, P04): source + message key + revision. The legacy id stays unique for old readers;
        // the content hash is a similarity index only (plan §5.2: a new post with the same text is still a new post).
        b.HasIndex(x => new { x.SourceId, x.SourceMessageKey, x.SourceRevision }).IsUnique();
        b.HasIndex(x => new { x.SourceId, x.SourceMessageId }).IsUnique();
        b.HasIndex(x => x.Hash);

        b.HasIndex(x => x.ProcessingStatus).HasFilter("processing_status = 0");
        // The sweep takes pending messages oldest-published first; the watchdog asks for the oldest pending one.
        b.HasIndex(x => x.PublishedAt, "ix_raw_messages_pending_published").HasDatabaseName("ix_raw_messages_pending_published").HasFilter("processing_status = 0");
        // Expired claims (processor crashed mid-message) are found by claimed_at; only the few InProgress rows are indexed.
        b.HasIndex(x => x.ClaimedAt, "ix_raw_messages_in_progress_claimed_at").HasDatabaseName("ix_raw_messages_in_progress_claimed_at").HasFilter("processing_status = 4");
        b.HasIndex(x => x.ReceivedAt).HasMethod("brin");
        b.HasIndex(x => x.PublishedAt).HasMethod("brin");

        b.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
