using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Rules;

namespace Puluj.Infrastructure.Seeding;

/// <summary>
/// Plan §8.3 (P08): bootstraps the rule catalog once. When no rule set exists, the file becomes v1 — validated and
/// published as the active set (actor <c>seed</c>). After that the database owns the rules: the file is never
/// re-applied, versions come from the authoring API.
/// </summary>
public sealed class EventKindRuleSeeder(SeedFiles files, IOptions<SeedOptions> options, RulesetService rulesets, ILogger<EventKindRuleSeeder> logger) : ISeeder
{
    public const string FileName = "taxonomy/event-rules.json";
    public const string Actor = "seed";

    /// <summary>After the event kinds (30): rules reference them.</summary>
    public int Order => 40;

    public async Task SeedAsync(PulujDbContext db, CancellationToken ct)
    {
        if (!options.Value.SeedEventKindRules)
        {
            return;
        }
        if (await db.EventKindRulesets.AnyAsync(r => r.IsActive, ct))
        {
            return; // the database owns the rules once a version is active
        }
        var file = await files.ReadAsync<RulesetFile>(FileName, ct);
        if (file is null)
        {
            logger.LogWarning("{File} not found under {Root}; the resolver keeps the built-in rules", FileName, files.Root);
            return;
        }
        var reason = file.Reason ?? "bootstrap from " + FileName;
        // A bootstrap interrupted between its steps leaves the seed draft without an active set: finish it instead of leaving `builtin` forever.
        var unfinished = await db.EventKindRulesets.AsNoTracking().Where(r => r.CreatedBy == Actor && r.State == EventKindRuleset.Draft).Select(r => (int?)r.Version).FirstOrDefaultAsync(ct);
        if (unfinished is int v)
        {
            logger.LogWarning("Event kind rules: finishing the interrupted bootstrap of v{Version}", v);
        }
        else if (await db.EventKindRulesets.AnyAsync(ct))
        {
            logger.LogWarning("Event kind rules: versions exist but none is active (a rollback target is needed); the resolver keeps the built-in rules");
            return;
        }
        var version = unfinished ?? await rulesets.CreateDraftAsync(null, Actor, reason, ct);
        await rulesets.ReplaceRulesAsync(version, file.Rules, Actor, reason, ct);
        var report = await rulesets.PublishAsync(version, Actor, reason, ct);
        logger.LogInformation("Event kind rules: v{Version} published from {File} ({Rules} rules, {Warnings} validation warnings)", version, FileName, file.Rules.Count, report.Warnings.Count);
    }
}
