using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Infrastructure.Persistence.Configurations;

public class TargetConfiguration : IEntityTypeConfiguration<Target>
{
    public void Configure(EntityTypeBuilder<Target> b)
    {
        b.HasKey(x => x.TargetId);
        b.Property(x => x.IdentificationSource).HasMaxLength(256);
        b.Property(x => x.ParserVersion).HasMaxLength(32);
        b.Property(x => x.ParserMetadata).HasColumnType("jsonb");
        b.Property(x => x.Location).HasColumnType("geography (geometry, 4326)");

        b.HasIndex(x => x.Location).HasMethod("gist");
        b.HasIndex(x => x.ObservedAt).HasMethod("brin");
        b.HasIndex(x => new { x.TargetClassId, x.ObservedAt });
        // The linker's candidate scan: same category inside the class window. The BRIN on observed_at alone turns lossy
        // after bulk deletes (a rebuild) and then reads tens of thousands of rows per new target.
        b.HasIndex(x => new { x.TargetCategoryId, x.ObservedAt });
        b.HasIndex(x => x.RawMessageId);
        b.HasIndex(x => x.DuplicateOfTargetId);
        // Plan §8.2 candidate for map/feed queries by kind; with fresh statistics the planner also uses it for the backfill's
        // `event_kind_id IS NULL` pass (Index Only Scan; EXPLAIN evidence in P07-backfill-report.json).
        b.HasIndex(x => new { x.EventKindId, x.ObservedAt }).IsDescending(false, true);
        // P09: one targets row per observation (idempotent writers); legacy rows keep NULL. Built CONCURRENTLY in the migration.
        b.HasIndex(x => x.ObservationId).HasDatabaseName("ux_targets_observation_id").IsUnique().HasFilter("observation_id IS NOT NULL");

        b.HasOne(x => x.RawMessage).WithMany(x => x.Targets).HasForeignKey(x => x.RawMessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.LocationPlace).WithMany().HasForeignKey(x => x.LocationPlaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Place>().WithMany().HasForeignKey(x => x.OriginPlaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Place>().WithMany().HasForeignKey(x => x.DestinationPlaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TargetCategory>().WithMany().HasForeignKey(x => x.TargetCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TargetClass>().WithMany().HasForeignKey(x => x.TargetClassId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TargetFamily>().WithMany().HasForeignKey(x => x.TargetFamilyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TargetModel>().WithMany().HasForeignKey(x => x.TargetModelId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Target>().WithMany().HasForeignKey(x => x.DuplicateOfTargetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.EventKind).WithMany().HasForeignKey(x => x.EventKindId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Plan §8.2 event catalog. Code is the stable identity; category is stored as the contract string.</summary>
public class EventKindConfiguration : IEntityTypeConfiguration<EventKind>
{
    public void Configure(EntityTypeBuilder<EventKind> b)
    {
        b.HasKey(x => x.EventKindId);
        b.Property(x => x.Code).HasMaxLength(96);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.NameUk).HasMaxLength(160);
        b.Property(x => x.Category).HasMaxLength(16)
            .HasConversion(c => c.ToString().ToLowerInvariant(), s => Enum.Parse<EventKindCategory>(s, true));
        b.Property(x => x.DefaultSeverity).HasMaxLength(32);
        b.Property(x => x.StateModel).HasMaxLength(64);
        b.Property(x => x.RenderMode).HasMaxLength(32);
        b.Property(x => x.MapColor).HasMaxLength(32);
        b.Property(x => x.MapIcon).HasMaxLength(64);
        b.Property(x => x.DedupPolicy).HasColumnType("jsonb");
        b.Property(x => x.Presentation).HasColumnType("jsonb");
        b.Property(x => x.Metadata).HasColumnType("jsonb");
    }
}

public class AirAlertConfiguration : IEntityTypeConfiguration<AirAlert>
{
    public void Configure(EntityTypeBuilder<AirAlert> b)
    {
        b.HasKey(x => x.AirAlertId);
        b.Property(x => x.SourceAlertId).HasMaxLength(128);
        b.HasIndex(x => new { x.SourceId, x.SourceAlertId }).IsUnique();
        b.HasIndex(x => x.PlaceId).HasFilter("ended_at IS NULL");
        b.HasIndex(x => x.StartedAt).HasMethod("brin");
        // The correlation sink finds the interval a message opened or closed (an AlertChanged push per alert message).
        b.HasIndex(x => x.StartRawMessageId);
        b.HasIndex(x => x.EndRawMessageId);
        b.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
