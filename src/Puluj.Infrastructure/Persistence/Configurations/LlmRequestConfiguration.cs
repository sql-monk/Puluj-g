using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence.Configurations;

public class LlmRequestConfiguration : IEntityTypeConfiguration<LlmRequest>
{
    public void Configure(EntityTypeBuilder<LlmRequest> b)
    {
        b.HasKey(x => x.LlmRequestId);
        b.Property(x => x.Worker).HasMaxLength(64);
        b.Property(x => x.Model).HasMaxLength(128);
        b.Property(x => x.PromptVersion).HasMaxLength(64);
        b.Property(x => x.Outcome).HasMaxLength(32);
        b.Property(x => x.EstimatedCostUsd).HasPrecision(18, 9);
        b.HasIndex(x => x.OccurredAt).HasMethod("brin");
        b.HasIndex(x => new { x.SourceId, x.OccurredAt });
        b.HasIndex(x => x.RawMessageId);
        b.HasOne(x => x.RawMessage).WithMany().HasForeignKey(x => x.RawMessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
