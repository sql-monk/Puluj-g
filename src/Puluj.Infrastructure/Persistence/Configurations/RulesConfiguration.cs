using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Persistence.Configurations;

/// <summary>Plan §8.3 (P08): versioned rule sets. Version is the key; one active set (partial unique), one shadow set.</summary>
public class EventKindRulesetConfiguration : IEntityTypeConfiguration<EventKindRuleset>
{
    public void Configure(EntityTypeBuilder<EventKindRuleset> b)
    {
        b.ToTable("event_kind_rulesets", t =>
        {
            t.HasCheckConstraint("ck_event_kind_rulesets_state", "state IN ('draft', 'shadow', 'published', 'superseded')");
        });
        b.HasKey(x => x.Version);
        b.Property(x => x.Version).ValueGeneratedNever();
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.CreatedBy).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.PublishedBy).HasMaxLength(128);
        b.Property(x => x.Notes).HasColumnType("jsonb");
        b.HasIndex(x => x.IsActive).HasDatabaseName("ux_event_kind_rulesets_active").IsUnique().HasFilter("is_active");
        b.HasIndex(x => x.State).HasDatabaseName("ux_event_kind_rulesets_shadow").IsUnique().HasFilter("state = 'shadow'");
        b.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.RulesetVersion).OnDelete(DeleteBehavior.Cascade);
    }
}

public class EventKindRuleConfiguration : IEntityTypeConfiguration<EventKindRule>
{
    public void Configure(EntityTypeBuilder<EventKindRule> b)
    {
        b.ToTable("event_kind_rules");
        b.HasKey(x => x.RuleId);
        b.Property(x => x.RuleCode).HasMaxLength(128);
        b.Property(x => x.Language).HasMaxLength(8);
        b.Property(x => x.SourceScope).HasColumnType("jsonb");
        b.Property(x => x.PositivePatterns).HasColumnType("jsonb");
        b.Property(x => x.NegativePatterns).HasColumnType("jsonb");
        b.Property(x => x.ExtractionHints).HasColumnType("jsonb");
        b.Property(x => x.ConfidenceModifier).HasPrecision(4, 3);
        b.Property(x => x.Actor).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.HasIndex(x => new { x.RulesetVersion, x.RuleCode }).IsUnique();
        b.HasOne<EventKind>().WithMany().HasForeignKey(x => x.EventKindId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class EventKindRulesetAuditConfiguration : IEntityTypeConfiguration<EventKindRulesetAudit>
{
    public void Configure(EntityTypeBuilder<EventKindRulesetAudit> b)
    {
        b.ToTable("event_kind_ruleset_audit");
        b.HasKey(x => x.AuditId);
        b.Property(x => x.Action).HasMaxLength(32);
        b.Property(x => x.Actor).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasIndex(x => new { x.Version, x.At });
    }
}

/// <summary>Disagreements of the shadow set with the live one; no FK to raw_messages (retention of raw rows is independent).</summary>
public class EventKindRuleShadowConfiguration : IEntityTypeConfiguration<EventKindRuleShadow>
{
    public void Configure(EntityTypeBuilder<EventKindRuleShadow> b)
    {
        b.ToTable("event_kind_rule_shadow");
        b.HasKey(x => x.ShadowId);
        b.Property(x => x.LiveKind).HasMaxLength(96);
        b.Property(x => x.ShadowKind).HasMaxLength(96);
        b.Property(x => x.LiveRule).HasMaxLength(128);
        b.Property(x => x.ShadowRule).HasMaxLength(128);
        b.HasIndex(x => new { x.ShadowVersion, x.CreatedAt });
        b.HasIndex(x => new { x.RawMessageId, x.RunId });
    }
}
