using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>Taxonomy and place names in memory (small, rarely changing), refreshed every 10 minutes.</summary>
public sealed class ReferenceCache(IDbContextFactory<PulujDbContext> factory, ILogger<ReferenceCache> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record PlaceInfo(int Id, string Name, PlaceLevel Level, int? ParentId, string CountryCode, double Lon, double Lat, double RadiusKm, int Population);

    public IReadOnlyDictionary<int, TargetCategory> Categories { get; private set; } = new Dictionary<int, TargetCategory>();
    public IReadOnlyDictionary<int, TargetClass> Classes { get; private set; } = new Dictionary<int, TargetClass>();
    public IReadOnlyDictionary<int, TargetFamily> Families { get; private set; } = new Dictionary<int, TargetFamily>();
    public IReadOnlyDictionary<int, TargetModel> Models { get; private set; } = new Dictionary<int, TargetModel>();
    public IReadOnlyDictionary<int, PlaceInfo> Places { get; private set; } = new Dictionary<int, PlaceInfo>();
    /// <summary>Parent → children, rebuilt with <see cref="Places"/>: the descendant walk for alert history.</summary>
    private ILookup<int, int> _children = Array.Empty<PlaceInfo>().ToLookup(p => 0, p => p.Id);
    public IReadOnlyDictionary<int, Source> Sources { get; private set; } = new Dictionary<int, Source>();
    /// <summary>Plan §8.2 event catalog by id; the DTO mapper resolves targets.event_kind_id to its code from here.</summary>
    public IReadOnlyDictionary<int, EventKind> EventKinds { get; private set; } = new Dictionary<int, EventKind>();
    public TaxonomyDto Taxonomy { get; private set; } = new([]);

    public Task Ready => _ready.Task;

    public PlaceInfo? Place(int? id) => id is int i && Places.TryGetValue(i, out var p) ? p : null;

    public PlaceInfo? RegionOf(int? placeId)
    {
        var p = Place(placeId);
        for (var i = 0; i < 5 && p is not null; i++)
        {
            if (p.Level is PlaceLevel.Region or PlaceLevel.NamedArea or PlaceLevel.Country || (p.Level == PlaceLevel.City && p.ParentId is null))
            {
                return p;
            }
            p = Place(p.ParentId);
        }
        return null;
    }

    /// <summary>The place's parents, nearest first, up to the root: `[raion, oblast]` for a hromada, empty for an oblast.</summary>
    public IReadOnlyList<int> Ancestors(int? placeId) => Ancestors(Place, placeId);

    /// <summary>Every place under this one (raions, hromadas, settlements of an oblast), any depth; the place itself excluded.</summary>
    public IReadOnlyCollection<int> Descendants(int placeId) => Descendants(_children, placeId);

    /// <summary>The place, the places that cover it and the places inside it: every place an alert "concerning" it can sit on.</summary>
    public List<int> Related(int placeId)
    {
        var ids = new List<int> { placeId };
        ids.AddRange(Ancestors(placeId));
        ids.AddRange(Descendants(placeId));
        return ids;
    }

    /// <summary>Hierarchy depth guard: the gazetteer nests at most country → oblast → raion → hromada → settlement → part.</summary>
    private const int MaxDepth = 8;

    public static IReadOnlyList<int> Ancestors(Func<int?, PlaceInfo?> place, int? placeId)
    {
        var chain = new List<int>();
        var p = place(placeId);
        for (var i = 0; i < MaxDepth && p?.ParentId is int parent; i++)
        {
            // A bad parent pointer (loop) must not spin: stop at the first repeat.
            if (parent == placeId || chain.Contains(parent))
            {
                break;
            }
            chain.Add(parent);
            p = place(parent);
        }
        return chain;
    }

    public static IReadOnlyCollection<int> Descendants(ILookup<int, int> children, int placeId)
    {
        var found = new HashSet<int>();
        var queue = new Queue<(int Id, int Depth)>();
        queue.Enqueue((placeId, 0));
        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (depth >= MaxDepth)
            {
                continue;
            }
            foreach (var child in children[id])
            {
                if (child != placeId && found.Add(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
        return found;
    }

    public SpeedProfileDto SpeedProfile(int? classId, int? modelId)
    {
        var cls = classId is int c && Classes.TryGetValue(c, out var k) ? k.Metadata : null;
        var model = modelId is int m && Models.TryGetValue(m, out var md) ? md.Metadata : null;
        return new SpeedProfileDto(
            Num(model, "speedKmhMin") ?? Num(cls, "speedKmhMin"),
            Num(model, "speedKmhMax") ?? Num(cls, "speedKmhMax"),
            Bool(cls, "etaEnabled") ?? false);
    }

    public (string DisplayMode, int FadeMinutes) Display(int? classId)
    {
        var cls = classId is int c && Classes.TryGetValue(c, out var k) ? k.Metadata : null;
        return (Str(cls, "displayMode") ?? "uav", (int)(Num(cls, "fadeMinutes") ?? 20));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reference cache refresh failed");
            }
            // Until the Worker has seeded the gazetteer there is nothing to cache: poll faster.
            await Task.Delay(Places.Count == 0 ? TimeSpan.FromSeconds(15) : RefreshInterval, ct);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        Categories = await db.TargetCategories.AsNoTracking().ToDictionaryAsync(x => x.TargetCategoryId, ct);
        Classes = await db.TargetClasses.AsNoTracking().ToDictionaryAsync(x => x.TargetClassId, ct);
        Families = await db.TargetFamilies.AsNoTracking().ToDictionaryAsync(x => x.TargetFamilyId, ct);
        Models = await db.TargetModels.AsNoTracking().ToDictionaryAsync(x => x.TargetModelId, ct);
        Sources = await db.Sources.AsNoTracking().ToDictionaryAsync(x => x.SourceId, ct);
        EventKinds = await db.EventKinds.AsNoTracking().ToDictionaryAsync(x => x.EventKindId, ct);
        Places = (await db.Places.AsNoTracking()
                .Select(p => new { p.PlaceId, p.Name, p.Level, p.ParentId, p.CountryCode, p.Centroid, p.RadiusKm, p.Population })
                .ToListAsync(ct))
            .ToDictionary(p => p.PlaceId, p => new PlaceInfo(p.PlaceId, p.Name, p.Level, p.ParentId, p.CountryCode, p.Centroid.X, p.Centroid.Y, p.RadiusKm, p.Population ?? 0));
        _children = Places.Values.Where(p => p.ParentId is not null).ToLookup(p => p.ParentId!.Value, p => p.Id);
        Taxonomy = BuildTaxonomy();
        _ready.TrySetResult();
        logger.LogInformation("Reference cache: {Models} models, {Places} places", Models.Count, Places.Count);
    }

    private TaxonomyDto BuildTaxonomy() => new(Categories.Values.OrderBy(c => c.TargetCategoryId).Select(c => new TaxonomyCategoryDto(
        c.TargetCategoryId, c.Code, c.Name,
        Classes.Values.Where(k => k.TargetCategoryId == c.TargetCategoryId).OrderBy(k => k.TargetClassId).Select(k =>
        {
            var (display, fade) = Display(k.TargetClassId);
            return new TaxonomyClassDto(k.TargetClassId, k.Code, k.Name, display, fade, SpeedProfile(k.TargetClassId, null),
                Families.Values.Where(f => f.TargetClassId == k.TargetClassId).OrderBy(f => f.TargetFamilyId).Select(f =>
                    new TaxonomyFamilyDto(f.TargetFamilyId, f.Code, f.Name,
                        Models.Values.Where(m => m.TargetFamilyId == f.TargetFamilyId && m.Enabled).OrderBy(m => m.TargetModelId).Select(m =>
                            new TaxonomyModelDto(m.TargetModelId, m.Code, m.CanonicalName, m.Manufacturer, m.Country, SpeedProfile(k.TargetClassId, m.TargetModelId))).ToList())).ToList());
        }).ToList())).ToList());

    private static double? Num(JsonDocument? doc, string name) =>
        doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonDocument? doc, string name) =>
        doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? Str(JsonDocument? doc, string name) =>
        doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
