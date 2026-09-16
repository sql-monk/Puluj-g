using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence.Configurations;

/// <summary>Plan §8.4 (P10): incidents, their evidence links (one incident per observation) and revisions (unique per incident).</summary>
public class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> b)
    {
        b.ToTable("incidents", t => t.HasCheckConstraint("ck_incidents_state", "state IN ('reported', 'confirmed', 'resolved', 'retracted')"));
        b.HasKey(x => x.IncidentId);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.ClosureReason).HasMaxLength(64);
        b.Property(x => x.Geometry).HasColumnType("geography (geometry, 4326)");
        b.HasIndex(x => new { x.EventKindId, x.EventAt });
        b.HasIndex(x => new { x.State, x.LastReportedAt });
        b.HasIndex(x => x.GenerationId);
        b.HasIndex(x => x.Geometry).HasMethod("gist");
        b.HasOne(x => x.EventKind).WithMany().HasForeignKey(x => x.EventKindId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Observations).WithOne(o => o.Incident).HasForeignKey(o => o.IncidentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class IncidentObservationConfiguration : IEntityTypeConfiguration<IncidentObservation>
{
    public void Configure(EntityTypeBuilder<IncidentObservation> b)
    {
        b.ToTable("incident_observations");
        b.HasKey(x => new { x.IncidentId, x.ObservationId });
        b.HasIndex(x => x.ObservationId).IsUnique(); // an observation is evidence of one incident
        b.Property(x => x.Relation).HasMaxLength(16);
        b.Property(x => x.PolicyVersion).HasMaxLength(32);
        b.Property(x => x.DecisionReason).HasColumnType("jsonb");
        // legacy_target_id without an FK: the legacy reset deletes targets; evidence links outlive the compatibility projection (ADR-0010).
    }
}

public class IncidentRevisionConfiguration : IEntityTypeConfiguration<IncidentRevision>
{
    public void Configure(EntityTypeBuilder<IncidentRevision> b)
    {
        b.ToTable("incident_revisions");
        b.HasKey(x => x.IncidentRevisionId);
        b.HasIndex(x => new { x.IncidentId, x.Revision }).IsUnique();
        b.Property(x => x.Change).HasMaxLength(16);
        b.Property(x => x.Actor).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Snapshot).HasColumnType("jsonb");
        b.HasOne(x => x.Incident).WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
    }
}
