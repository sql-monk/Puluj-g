using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Rules;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing.Indexes;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Rules;

namespace Puluj.Integration.Tests;

/// <summary>
/// P08 / plan §8.3 on a real PostGIS: seed bootstrap of rule-set v1 (idempotent), the authoring flow with its audit and
/// the one-active invariant, the index provider's pin/shadow pointers, the legacy pipeline citing the pinned version,
/// and the migration's Down/Up. Tests share the fixture's database, so each one leaves the rule catalog as it found it
/// (v1 active, nothing else) — see <see cref="ResetRulesetsAsync"/>.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class P08RulesetTests(PipelineFixture fixture)
{
    private ServiceProvider Services => fixture.Services!;
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private RulesetService Rulesets => Services.GetRequiredService<RulesetService>();
    private const string Actor = "p08-test";

    private static readonly RuleDefinition Fire = new("fire.pozhezh", "fire.reported", "*",
        [new PatternDefinition("stems", ["пожеж"], 5)], [new PatternDefinition("stems", ["пожежн", "небезпек"], 3)], 500);

    [Fact]
    public async Task Seed_bootstraps_v1_once_and_never_reapplies()
    {
        await ResetRulesetsAsync();
        await using var db = await Factory.CreateDbContextAsync();
        var v1 = await db.EventKindRulesets.AsNoTracking().Include(r => r.Rules).SingleAsync();
        Assert.Equal(1, v1.Version);
        Assert.Equal(EventKindRuleset.Published, v1.State);
        Assert.True(v1.IsActive);
        Assert.Equal(EventKindRuleSeeder.Actor, v1.CreatedBy);
        Assert.Equal(24, v1.Rules.Count);
        Assert.Equal(2, await db.EventKindRulesetAudits.CountAsync(a => a.Version == 1 && a.Action == EventKindRulesetAudit.Validated || a.Version == 1 && a.Action == EventKindRulesetAudit.PublishedAction));

        // A newer file would not touch the database: the seeder only bootstraps.
        var seeder = Services.GetServices<ISeeder>().OfType<EventKindRuleSeeder>().Single();
        await seeder.SeedAsync(db, CancellationToken.None);
        Assert.Equal(1, await db.EventKindRulesets.CountAsync());
        Assert.Equal(24, await db.EventKindRules.CountAsync());
    }

    [Fact]
    public async Task Authoring_flow_draft_rules_validate_shadow_publish_rollback_with_audit_and_one_active()
    {
        await ResetRulesetsAsync();
        var v2 = await Rulesets.CreateDraftAsync(null, Actor, "add fire", CancellationToken.None);
        Assert.Equal(2, v2);
        var details = await Rulesets.GetAsync(v2, CancellationToken.None);
        Assert.Equal(24, details.Rules.Count); // copied from the active v1
        Assert.Equal(EventKindRuleset.Draft, details.Summary.State);

        // Replace: v1 rules unchanged keep rule_version 1; the new one starts at 1; a changed one bumps.
        var rules = details.Rules.Select(r => r.RuleCode == "event:вибух" ? r with { Priority = r.Priority + 1 } : r).Append(Fire).ToList();
        await Rulesets.ReplaceRulesAsync(v2, rules, Actor, "rules", CancellationToken.None);
        details = await Rulesets.GetAsync(v2, CancellationToken.None);
        Assert.Equal(25, details.Rules.Count);
        Assert.Equal(1, details.Rules.Single(r => r.RuleCode == "fire.pozhezh").RuleVersion);
        await Rulesets.ReplaceRulesAsync(v2, rules.Select(r => r.RuleCode == "fire.pozhezh" ? r with { RuleVersion = 7 } : r).ToList(), Actor, "rules", CancellationToken.None);
        details = await Rulesets.GetAsync(v2, CancellationToken.None);
        Assert.Equal(1, details.Rules.Single(r => r.RuleCode == "fire.pozhezh").RuleVersion); // never taken from the payload
        Assert.Equal(2, details.Rules.Single(r => r.RuleCode == "event:вибух").RuleVersion);
        Assert.Equal(1, details.Rules.Single(r => r.RuleCode == "event:відбій_тривог").RuleVersion);

        var report = await Rulesets.ValidateAsync(v2, Actor, CancellationToken.None);
        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(e => e.Message)));

        // Wrong states are refused, not defaulted.
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.ReplaceRulesAsync(1, rules, Actor, "x", CancellationToken.None)); // published
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.RollbackAsync(v2, Actor, "x", CancellationToken.None)); // a draft cannot be "rolled back to"
        await Assert.ThrowsAsync<RulesetNotFoundException>(() => Rulesets.GetAsync(99, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Rulesets.PublishAsync(v2, "", "", CancellationToken.None));

        await Rulesets.StartShadowAsync(v2, Actor, "shadow it", CancellationToken.None);
        var v3 = await Rulesets.CreateDraftAsync(1, Actor, "second draft", CancellationToken.None);
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.StartShadowAsync(v3, Actor, "x", CancellationToken.None)); // one shadow at a time
        await using (var db = await Factory.CreateDbContextAsync())
        {
            // ...and the partial unique index says so even without the service.
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(async () =>
            {
                var row = await db.EventKindRulesets.SingleAsync(r => r.Version == v3);
                row.State = EventKindRuleset.Shadow;
                await db.SaveChangesAsync();
            });
        }
        // The way out of a bad shadow: stop it (back to draft), then shadow again.
        await Rulesets.StopShadowAsync(v2, Actor, "looks wrong", CancellationToken.None);
        Assert.Equal(EventKindRuleset.Draft, (await Rulesets.GetAsync(v2, CancellationToken.None)).Summary.State);
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.StopShadowAsync(v2, Actor, "x", CancellationToken.None)); // not shadowing
        await Rulesets.StartShadowAsync(v2, Actor, "shadow again", CancellationToken.None);

        // Publish validates again (a stale earlier validation is never trusted) and moves the active pointer.
        await Rulesets.PublishAsync(v2, Actor, "go live", CancellationToken.None);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var rows = await db.EventKindRulesets.AsNoTracking().OrderBy(r => r.Version).ToListAsync();
            Assert.Equal([EventKindRuleset.Superseded, EventKindRuleset.Published, EventKindRuleset.Draft], rows.Select(r => r.State));
            Assert.Equal([false, true, false], rows.Select(r => r.IsActive));
            Assert.Equal(Actor, rows[1].PublishedBy);
            var audit = await db.EventKindRulesetAudits.AsNoTracking().Where(a => a.Version == v2).OrderBy(a => a.AuditId).Select(a => a.Action).ToListAsync();
            Assert.Equal([EventKindRulesetAudit.Created, EventKindRulesetAudit.RulesReplaced, EventKindRulesetAudit.RulesReplaced, EventKindRulesetAudit.Validated, EventKindRulesetAudit.ShadowStarted, EventKindRulesetAudit.ShadowStopped, EventKindRulesetAudit.ShadowStarted, EventKindRulesetAudit.Validated, EventKindRulesetAudit.PublishedAction], audit);
        }
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.PublishAsync(v2, Actor, "again", CancellationToken.None)); // already published

        // A draft with an error cannot be published; the failed validation is on record.
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.ReplaceRulesAsync(v3, [Fire with { EventKindCode = "no.such.kind" }], Actor, "bad", CancellationToken.None));
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.ReplaceRulesAsync(v3, [Fire with { RuleCode = new string('x', 129) }], Actor, "bad", CancellationToken.None)); // column length, not a 500
        await Rulesets.ReplaceRulesAsync(v3, [Fire, Fire with { RuleCode = "fire.dup", EventKindCode = "damage.reported" }], Actor, "conflict", CancellationToken.None);
        var refused = await Assert.ThrowsAsync<RulesetValidationException>(() => Rulesets.PublishAsync(v3, Actor, "try", CancellationToken.None));
        Assert.Contains(refused.Report.Errors, e => e.Code == "conflicting_rules");
        await using (var db = await Factory.CreateDbContextAsync())
        {
            Assert.True(await db.EventKindRulesetAudits.AnyAsync(a => a.Version == v3 && a.Action == EventKindRulesetAudit.Validated));
            Assert.Equal(v2, await db.EventKindRulesets.Where(r => r.IsActive).Select(r => r.Version).SingleAsync());
        }

        // Rollback: v1 active again for new jobs, v2 superseded, an audit entry says from where.
        await Rulesets.RollbackAsync(1, Actor, "regression", CancellationToken.None);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var rows = await db.EventKindRulesets.AsNoTracking().OrderBy(r => r.Version).ToListAsync();
            Assert.Equal([EventKindRuleset.Published, EventKindRuleset.Superseded, EventKindRuleset.Draft], rows.Select(r => r.State));
            Assert.Equal([true, false, false], rows.Select(r => r.IsActive));
            var rolled = await db.EventKindRulesetAudits.AsNoTracking().SingleAsync(a => a.Version == 1 && a.Action == EventKindRulesetAudit.RolledBack);
            Assert.Equal(v2, rolled.Details!.RootElement.GetProperty("from_version").GetInt32());
            Assert.Equal(25, await db.EventKindRules.CountAsync(r => r.RulesetVersion == v2)); // stored versions are never rewritten
        }
        await Assert.ThrowsAsync<RulesetConflictException>(() => Rulesets.RollbackAsync(1, Actor, "x", CancellationToken.None)); // already active
        await ResetRulesetsAsync();
    }

    [Fact]
    public async Task Concurrent_publishes_leave_exactly_one_active()
    {
        await ResetRulesetsAsync();
        var a = await Rulesets.CreateDraftAsync(null, Actor, "a", CancellationToken.None);
        var b = await Rulesets.CreateDraftAsync(null, Actor, "b", CancellationToken.None);
        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                try
                {
                    await Rulesets.PublishAsync(i % 2 == 0 ? a : b, Actor, "race", CancellationToken.None);
                    return (Ok: true, Error: (string?)null);
                }
                catch (RulesetConflictException ex)
                {
                    return (Ok: false, Error: ex.Message);
                }
            })));
        Assert.True(results.Count(r => r.Ok) == 2, string.Join(" | ", results.Select(r => r.Error))); // each draft published once; the repeats saw "already published"
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.EventKindRulesets.CountAsync(r => r.IsActive));
        Assert.Equal(1, await db.EventKindRulesets.CountAsync(r => r.State == EventKindRuleset.Published));
        await ResetRulesetsAsync();
    }

    [Fact]
    public async Task Index_provider_pins_active_then_pin_then_shadow_and_the_legacy_pipeline_cites_it()
    {
        await ResetRulesetsAsync();
        var provider = Services.GetRequiredService<IndexProvider>();
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal("v1", provider.Rules.Id);
        Assert.Null(provider.ShadowRules);

        var v2 = await Rulesets.CreateDraftAsync(null, Actor, "shadow", CancellationToken.None);
        await Rulesets.ReplaceRulesAsync(v2, (await Rulesets.GetAsync(v2, CancellationToken.None)).Rules.Append(Fire).ToList(), Actor, "rules", CancellationToken.None);
        await Rulesets.StartShadowAsync(v2, Actor, "shadow", CancellationToken.None);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            await provider.RefreshRulesAsync(db, CancellationToken.None);
        }
        Assert.Equal("v1", provider.Rules.Id);
        Assert.Equal("v2", provider.ShadowRules!.Id);
        Assert.Contains(provider.ShadowRules.Rules, r => r.Code == "fire.pozhezh");

        // Pin: an unknown version → warning, the active set stays; a draft only with RulesetPinAllowDraft; a published one → pinned.
        var options = Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RulesetOptions>>().CurrentValue;
        try
        {
            await using var db = await Factory.CreateDbContextAsync();
            options.RulesetPin = 99;
            await provider.RefreshRulesAsync(db, CancellationToken.None);
            Assert.Equal("v1", provider.Rules.Id);
            options.RulesetPin = v2; // shadow (unpublished)
            await provider.RefreshRulesAsync(db, CancellationToken.None);
            Assert.Equal("v1", provider.Rules.Id);
            options.RulesetPinAllowDraft = true;
            await provider.RefreshRulesAsync(db, CancellationToken.None);
            Assert.Equal("v2", provider.Rules.Id);
        }
        finally
        {
            options.RulesetPin = null;
            options.RulesetPinAllowDraft = false;
            await using var db = await Factory.CreateDbContextAsync();
            await provider.RefreshRulesAsync(db, CancellationToken.None);
        }
        Assert.Equal("v1", provider.Rules.Id);

        // The legacy pipeline stamps the pinned version and the rule on the target's metadata.
        var ingestor = Services.GetRequiredService<RawMessageIngestor>();
        var processor = Services.GetRequiredService<RawMessageProcessor>();
        int sourceId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
        }
        var ingested = await ingestor.IngestAsync(new IncomingMessage { SourceId = sourceId, SourceMessageId = "p08-1", PublishedAt = DateTimeOffset.UtcNow, RawText = "Вибухи у Харкові.", RawPayload = JsonDocument.Parse("{}") },
            "tg_kpszsu", CancellationToken.None);
        Assert.Equal(1, await processor.ProcessAsync(ingested.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var target = await db.Targets.AsNoTracking().SingleAsync(t => t.RawMessageId == ingested.RawMessageId);
            Assert.Equal("v1", target.ParserMetadata!.RootElement.GetProperty("rulesetVersion").GetString());
            Assert.Equal("event:вибух", target.ParserMetadata.RootElement.GetProperty("ruleCode").GetString());
            Assert.Equal("impact.explosion.reported", target.ParserMetadata.RootElement.GetProperty("eventKindCode").GetString());
            Assert.NotNull(target.EventKindId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM targets WHERE raw_message_id = {0}; DELETE FROM raw_messages WHERE raw_message_id = {0}", ingested.RawMessageId.Value);
        }
        await ResetRulesetsAsync();
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Null(provider.ShadowRules);
    }

    [Fact]
    public async Task Migration_down_removes_rule_tables_and_up_restores_them_with_a_fresh_bootstrap()
    {
        await ResetRulesetsAsync();
        await using var db = await Factory.CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var index = applied.FindIndex(m => m.EndsWith("_AddEventKindRules", StringComparison.Ordinal));
        Assert.True(index > 0);
        await migrator.MigrateAsync(applied[index - 1]);
        foreach (var table in new[] { "event_kind_rulesets", "event_kind_rules", "event_kind_ruleset_audit", "event_kind_rule_shadow" })
        {
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass({0}) IS NOT NULL AS \"Value\"", table).SingleAsync(), table);
        }
        await migrator.MigrateAsync();
        Assert.Equal(0, await db.EventKindRulesets.CountAsync());
        foreach (var seeder in Services.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(db, CancellationToken.None);
        }
        Assert.Equal(1, await db.EventKindRulesets.CountAsync(r => r.Version == 1 && r.IsActive));
        await Services.GetRequiredService<IndexProvider>().RefreshAsync(CancellationToken.None);
    }

    /// <summary>Back to the seeded state: v1 alone, published and active.</summary>
    private async Task ResetRulesetsAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE event_kind_rule_shadow; DELETE FROM event_kind_ruleset_audit; DELETE FROM event_kind_rulesets");
        var seeder = Services.GetServices<ISeeder>().OfType<EventKindRuleSeeder>().Single();
        await seeder.SeedAsync(db, CancellationToken.None);
    }
}
