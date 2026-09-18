using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Rules;
using Puluj.Processing.Rules;

namespace Puluj.Processing.Indexes;

/// <summary>Loads taxonomy and gazetteer snapshots from the database and refreshes them periodically (spec §7: taxonomy lives in the DB).</summary>
public sealed class IndexProvider(IDbContextFactory<PulujDbContext> factory, IOptionsMonitor<RulesetOptions> rulesetOptions, ILogger<IndexProvider> logger) : BackgroundService, IIndexes
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pinWarned;

    public TaxonomyIndex Taxonomy { get; private set; } = TaxonomyIndex.Empty;
    public GazetteerIndex Gazetteer { get; private set; } = GazetteerIndex.Empty;
    public EventKindIndex EventKinds { get; private set; } = EventKindIndex.Empty;
    public RulesetIndex Rules { get; private set; } = RulesetIndex.Builtin;

    /// <summary>Completes after the first successful load.</summary>
    public Task Ready => _ready.Task;

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        Taxonomy = await LoadTaxonomyAsync(db, ct);
        Gazetteer = await LoadGazetteerAsync(db, ct);
        EventKinds = await LoadEventKindsAsync(db, ct);
        await RefreshRulesAsync(db, ct);
        _ready.TrySetResult();
        logger.LogInformation("Indexes loaded: {Aliases} aliases, {Places} places, {Kinds} event kinds, rules {Ruleset}", Taxonomy.Aliases.Count, Gazetteer.Count, EventKinds.Count, Rules.Id);
    }

    /// <summary>
    /// Re-reads the active (or pinned) rule set when its version changes. Cheap enough for the short
    /// poll: a version pointer query, and the rules only when the pointer moved. The pin never crashes the worker:
    /// an unknown or unpublished pin logs a warning once and the active set is used.
    /// </summary>
    public async Task RefreshRulesAsync(PulujDbContext db, CancellationToken ct)
    {
        var o = rulesetOptions.CurrentValue;
        var active = await db.EventKindRulesets.AsNoTracking().Where(r => r.IsActive).Select(r => (int?)r.Version).SingleOrDefaultAsync(ct);
        var target = active;
        if (o.RulesetPin is int pin)
        {
            var pinned = await db.EventKindRulesets.AsNoTracking().Where(r => r.Version == pin).Select(r => new { r.State }).SingleOrDefaultAsync(ct);
            if (pinned is not null && (o.RulesetPinAllowDraft || pinned.State is EventKindRuleset.Published or EventKindRuleset.Superseded))
            {
                target = pin;
            }
            else if (Interlocked.Exchange(ref _pinWarned, 1) == 0)
            {
                logger.LogWarning("Parsing:RulesetPin={Pin} is {Why}; using the active rule set {Active}", pin, pinned is null ? "unknown" : pinned.State, active is int a ? $"v{a}" : "builtin");
            }
        }
        if (target != Rules.Version || (target is null && !Rules.IsBuiltin))
        {
            Rules = target is int v && await RulesetService.LoadAsync(db, v, ct) is { } data ? RulesetIndex.From(data) : RulesetIndex.Builtin;
            logger.LogInformation("Rule set pinned: {Ruleset} ({State}, {Rules} rules)", Rules.Id, Rules.State, Rules.Rules.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastFull = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastFull >= (EventKinds.IsEmpty ? TimeSpan.FromSeconds(15) : RefreshInterval))
                {
                    await RefreshAsync(ct);
                    lastFull = DateTimeOffset.UtcNow;
                }
                else
                {
                    await using var db = await factory.CreateDbContextAsync(ct);
                    await RefreshRulesAsync(db, ct); // publish/rollback propagate within the short poll (P08)
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Index refresh failed");
            }
            // Until the seeder has filled the catalog (fresh database) targets would get no kind: poll faster, like ReferenceCache.
            await Task.Delay(TimeSpan.FromSeconds(EventKinds.IsEmpty ? 15 : Math.Max(5, rulesetOptions.CurrentValue.RulesetPollSeconds)), ct);
        }
    }

    public static async Task<EventKindIndex> LoadEventKindsAsync(PulujDbContext db, CancellationToken ct) =>
        new(await db.EventKinds.AsNoTracking().ToListAsync(ct));

    public static async Task<TaxonomyIndex> LoadTaxonomyAsync(PulujDbContext db, CancellationToken ct)
    {
        var categories = await db.TargetCategories.AsNoTracking().ToListAsync(ct);
        var classes = await db.TargetClasses.AsNoTracking().ToListAsync(ct);
        var families = await db.TargetFamilies.AsNoTracking().ToListAsync(ct);
        var models = await db.TargetModels.AsNoTracking().Where(m => m.Enabled).ToListAsync(ct);
        var aliases = await db.TargetModelAliases.AsNoTracking().ToListAsync(ct);

        var classById = classes.ToDictionary(c => c.TargetClassId);
        var familyById = families.ToDictionary(f => f.TargetFamilyId);
        var refs = new Dictionary<(AliasTargetLevel, int), TargetRef>();
        foreach (var c in categories)
        {
            refs[(AliasTargetLevel.Category, c.TargetCategoryId)] = new TargetRef(c.TargetCategoryId, null, null, null, c.Code, c.Name, AliasTargetLevel.Category);
        }
        foreach (var c in classes)
        {
            refs[(AliasTargetLevel.Class, c.TargetClassId)] = new TargetRef(c.TargetCategoryId, c.TargetClassId, null, null, c.Code, c.Name, AliasTargetLevel.Class);
        }
        foreach (var f in families)
        {
            var c = classById[f.TargetClassId];
            refs[(AliasTargetLevel.Family, f.TargetFamilyId)] = new TargetRef(c.TargetCategoryId, c.TargetClassId, f.TargetFamilyId, null, f.Code, f.Name, AliasTargetLevel.Family);
        }
        foreach (var m in models)
        {
            var f = familyById[m.TargetFamilyId];
            var c = classById[f.TargetClassId];
            refs[(AliasTargetLevel.Model, m.TargetModelId)] = new TargetRef(c.TargetCategoryId, c.TargetClassId, f.TargetFamilyId, m.TargetModelId, m.Code, m.CanonicalName, AliasTargetLevel.Model);
        }

        var entries = aliases
            .Where(a => refs.ContainsKey((a.TargetLevel, a.TargetId)))
            .Select(a => new AliasEntry(a.Alias, a.Alias.Split(' ', StringSplitOptions.RemoveEmptyEntries), a.ExactMatch,
                a.TargetLevel, a.TargetId, a.Priority, a.ImpliedConfidence, a.Language, a.SourceId))
            .OrderByDescending(a => a.Words.Length)
            .ThenByDescending(a => a.Priority)
            .ToList();

        var profiles = classes.ToDictionary(c => c.TargetClassId, c => ClassProfile.FromMetadata(c.TargetClassId, c.Code, c.Metadata));
        return new TaxonomyIndex(entries, refs, profiles,
            categories.ToDictionary(c => c.Code, c => c.TargetCategoryId),
            classes.ToDictionary(c => c.Code, c => c.TargetClassId));
    }

    public static async Task<GazetteerIndex> LoadGazetteerAsync(PulujDbContext db, CancellationToken ct)
    {
        var rows = await db.Places.AsNoTracking()
            .Select(p => new { p.PlaceId, p.Name, p.Level, p.ParentId, p.CountryCode, p.Population, p.Centroid, p.RadiusKm, p.NameVariants, p.Geometry })
            .ToListAsync(ct);
        // Only areal geometries are kept as boundaries; settlements are points and their centroid already says it all.
        // Boundaries are simplified (~1 km for oblasts, ~200 m below): the correlator measures polygon-to-polygon gaps
        // for every candidate, which is quadratic in vertex count, and a report "on the oblast" is not a 1 km fact.
        return new GazetteerIndex(rows.Select(r =>
            (new PlaceEntry(r.PlaceId, r.Name, r.Level, r.ParentId, r.CountryCode, r.Population ?? 0, r.Centroid, r.RadiusKm,
                r.Geometry is NetTopologySuite.Geometries.IPolygonal ? Simplify(r.Geometry, r.Level) : null), r.NameVariants)));
    }

    private static NetTopologySuite.Geometries.Geometry Simplify(NetTopologySuite.Geometries.Geometry g, PlaceLevel level)
    {
        var tolerance = level is PlaceLevel.Region or PlaceLevel.Country ? 0.01 : 0.002;
        var s = NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(g, tolerance);
        s.SRID = g.SRID;
        return s.IsEmpty ? g : s;
    }
}
