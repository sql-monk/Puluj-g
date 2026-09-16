using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using Puluj.Api;
using Puluj.Api.Services;
using Puluj.Contracts;

namespace Puluj.Api.Tests;

public sealed class MapFilterTests
{
    [Fact]
    public void U06_parses_canonical_comma_filters_without_treating_them_as_presentation_state()
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?sourceIds=7,9&categoryIds=2&classIds=3&eventKinds=explosion.reported&entityKinds=track,observation&regionId=17&status=Active&confidence=High"));
        var filter = MapFilter.From(query);

        Assert.True(filter.IsFiltered);
        Assert.Equal([7, 9], filter.SourceIds.Order());
        Assert.Contains(2, filter.CategoryIds);
        Assert.Contains(3, filter.ClassIds);
        Assert.Contains("explosion.reported", filter.EventKinds);
        Assert.True(filter.Includes("track"));
        Assert.False(filter.Includes("alert"));
        Assert.Equal(17, filter.RegionId);
    }

    [Fact]
    public void U06_rejects_an_oversized_replay_window_instead_of_silently_clamping()
    {
        var error = new MapWindowTooLargeException(TimeSpan.FromHours(36));
        Assert.Equal(TimeSpan.FromHours(36), error.Maximum);
        Assert.Contains("36", error.Message);
    }

    [Fact]
    public void U06_map_transport_writes_bigint_ids_as_decimal_strings()
    {
        var options = new JsonSerializerOptions();
        ApiDependencyInjection.ConfigureMapJson(options);
        var replay = new ReplayDto(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), [new ReplayTrackDto(9_007_199_254_740_993, new TargetTypeDto("target", "Ціль", null, null, null, null, null, null, "uav", 15, new SpeedProfileDto(null, null, false)), [])]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(replay, options));
        var id = json.RootElement.GetProperty("tracks")[0].GetProperty("id");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("9007199254740993", id.GetString());
    }
}
