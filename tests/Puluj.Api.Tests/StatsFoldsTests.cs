using Puluj.Api.Services;
using Puluj.Domain.Enums;

namespace Puluj.Api.Tests;

public class StatsFoldsTests
{
    private static ReferenceCache.PlaceInfo Place(int id, string name, PlaceLevel level, int? parent = null) =>
        new(id, name, level, parent, "UA", 30, 50, 10, 0);

    [Fact]
    public void CategoryIndex_UnknownCodesFoldIntoTheLastSlot()
    {
        Assert.Equal(0, StatsFolds.CategoryIndex("UAV"));
        Assert.Equal(1, StatsFolds.CategoryIndex("MISSILE"));
        Assert.Equal(StatsFolds.CategoryOrder.Length - 1, StatsFolds.CategoryIndex("UNKNOWN"));
        Assert.Equal(StatsFolds.CategoryOrder.Length - 1, StatsFolds.CategoryIndex(null));
        Assert.Equal(StatsFolds.CategoryOrder.Length - 1, StatsFolds.CategoryIndex("SOMETHING_NEW"));
    }

    [Fact]
    public void ByRegion_FoldsPlacesIntoRegionsKeepsTopSumsTheRestAndCountsTheUnlocated()
    {
        var sumy = Place(1, "Сумська область", PlaceLevel.Region);
        var kyiv = Place(2, "Київська область", PlaceLevel.Region);
        var odesa = Place(3, "Одеська область", PlaceLevel.Region);
        var regions = new Dictionary<int, ReferenceCache.PlaceInfo> { [10] = sumy, [11] = sumy, [20] = kyiv, [30] = odesa };
        var rows = new List<(int?, long)> { (10, 5), (11, 7), (20, 4), (30, 1), (99, 100), (null, 3) };
        var (result, unlocated) = StatsFolds.ByRegion(rows, id => id is int i ? regions.GetValueOrDefault(i) : null, 2);
        Assert.Equal(3, result.Count);
        Assert.Equal((1, "Сумська область", 12), (result[0].Id, result[0].Name, result[0].Targets));
        Assert.Equal((2, "Київська область", 4), (result[1].Id, result[1].Name, result[1].Targets));
        Assert.Null(result[2].Id);
        Assert.Equal(StatsFolds.OtherName, result[2].Name);
        Assert.Equal(1, result[2].Targets);
        Assert.Equal(103, unlocated); // the unresolved 99 and the null place are not "other", they are not located
    }

    [Fact]
    public void Routes_FoldsPlacePairsIntoRegionPairs()
    {
        var sumy = Place(1, "Сумська", PlaceLevel.Region);
        var kyiv = Place(2, "Київська", PlaceLevel.Region);
        var regions = new Dictionary<int, ReferenceCache.PlaceInfo> { [10] = sumy, [11] = sumy, [20] = kyiv };
        var rows = new List<(int, int, long)> { (10, 20, 3), (11, 20, 2), (20, 10, 1), (10, 99, 50) };
        var result = StatsFolds.Routes(rows, id => id is int i ? regions.GetValueOrDefault(i) : null, 10);
        Assert.Equal(2, result.Count);
        Assert.Equal((1, 2, 5), (result[0].FromId, result[0].ToId, result[0].Count));
        Assert.Equal((2, 1, 1), (result[1].FromId, result[1].ToId, result[1].Count));
    }

    [Fact]
    public void Slices_KeepEnumOrderDropZerosAndLabelValues()
    {
        var rows = new List<(int, long)> { ((int)EventType.ExplosionReport, 2), ((int)EventType.TargetObserved, 10), (999, 1) };
        var slices = StatsFolds.Slices(rows, StatsFolds.EventTypeLabels);
        Assert.Equal(["TargetObserved", "ExplosionReport"], slices.Select(s => s.Key));
        Assert.Equal("вибухи", slices[1].Label);
        Assert.Equal(10, slices[0].Count);
    }

    [Fact]
    public void Folds_KeepCountsAboveInt32MaxValue()
    {
        const long many = 3_000_000_000;
        var place = Place(1, "Сумська", PlaceLevel.Region);
        var (regions, unlocated) = StatsFolds.ByRegion([(10, many)], id => id == 10 ? place : null, 15);
        Assert.Equal(many, Assert.Single(regions).Targets);
        Assert.Equal(0, unlocated);
        Assert.Equal(many, StatsFolds.Slices(new List<(int Value, long Count)> { ((int)EventType.TargetObserved, many) }, StatsFolds.EventTypeLabels).Single().Count);
    }
}
