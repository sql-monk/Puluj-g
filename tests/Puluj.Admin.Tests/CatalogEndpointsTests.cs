using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Puluj.Admin.Endpoints;
using Puluj.Domain.Entities;

namespace Puluj.Admin.Tests;

/// <summary>The catalog editor's validation, legacy-mapping guard, and audit snapshot.</summary>
public class CatalogEndpointsTests
{
    private static CatalogEndpoints.UpdateRequest Req(string? actor = "ops", string? reason = "why", string? color = null, string? icon = null, string? lifetime = null, string? mode = null, int? sort = null, string? name = null) =>
        new(name, null, color, icon, lifetime, mode, sort, null, null, actor, reason, null);

    private static int? Status(IResult? r) => r is null ? null : ((IStatusCodeHttpResult)r).StatusCode;

    [Fact]
    public void Actor_and_reason_are_required()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, Status(CatalogEndpoints.Validate(Req(actor: null))));
        Assert.Equal(StatusCodes.Status400BadRequest, Status(CatalogEndpoints.Validate(Req(reason: " "))));
        Assert.Null(CatalogEndpoints.Validate(Req()));
    }

    [Theory]
    [InlineData("#fb8c00", null, null, null, true)]
    [InlineData("orange", null, null, null, false)]
    [InlineData("#fb8c0", null, null, null, false)]
    [InlineData(null, "fire", null, null, true)]
    [InlineData(null, "rocket", null, null, false)]
    [InlineData(null, null, "02:00:00", null, true)]
    [InlineData(null, null, "1.12:00:00", null, true)]
    [InlineData(null, null, "00:00:30", null, false)]
    [InlineData(null, null, "8.00:00:00", null, false)]
    [InlineData(null, null, "soon", null, false)]
    [InlineData(null, null, null, "area", true)]
    [InlineData(null, null, null, "sprite", false)]
    public void Presentation_fields_are_validated(string? color, string? icon, string? lifetime, string? mode, bool ok)
    {
        var result = CatalogEndpoints.Validate(Req(color: color, icon: icon, lifetime: lifetime, mode: mode));
        Assert.Equal(ok, result is null);
        if (!ok)
        {
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, Status(result));
        }
    }

    [Fact]
    public void Only_a_presentation_field_makes_the_row_admin_owned()
    {
        Assert.False(CatalogEndpoints.HasPresentationChange(new CatalogEndpoints.UpdateRequest(null, null, null, null, null, null, null, false, null, "ops", "off", null))); // enabled-only: the seed keeps the presentation
        Assert.True(CatalogEndpoints.HasPresentationChange(Req(color: "#123456")));
        Assert.True(CatalogEndpoints.HasPresentationChange(Req(sort: 3)));
        Assert.Contains("air-defence", CatalogEndpoints.IconVocabulary); // every icon the seed uses is in the vocabulary (seed and editor cannot drift)
        Assert.Contains("alert-off", CatalogEndpoints.IconVocabulary);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Status(CatalogEndpoints.Validate(Req(lifetime: "2")))); // exact formats only
    }

    [Fact]
    public void Name_and_sort_order_have_bounds()
    {
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Status(CatalogEndpoints.Validate(Req(name: "  "))));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Status(CatalogEndpoints.Validate(Req(sort: -1))));
        Assert.Null(CatalogEndpoints.Validate(Req(name: "Пожежа", sort: 50)));
    }

    [Fact]
    public void Legacy_mapping_is_read_from_metadata_and_the_snapshot_carries_the_admin_owned_fields()
    {
        var kind = new EventKind { Code = "impact.explosion.reported", NameUk = "Вибух", Metadata = JsonDocument.Parse("""{"legacyEventType": "ExplosionReport"}"""), MapColor = "#fb8c00", MapLifetime = TimeSpan.FromHours(2), SortOrder = 40 };
        Assert.True(CatalogEndpoints.HasLegacyMapping(kind));
        Assert.False(CatalogEndpoints.HasLegacyMapping(new EventKind { Code = "fire.reported", NameUk = "Пожежа" }));
        var snapshot = CatalogEndpoints.Snapshot(kind).RootElement;
        Assert.Equal("#fb8c00", snapshot.GetProperty("mapColor").GetString());
        Assert.Equal("02:00:00", snapshot.GetProperty("mapLifetime").GetString());
        Assert.Equal(40, snapshot.GetProperty("sortOrder").GetInt32());
        Assert.True(snapshot.GetProperty("enabled").GetBoolean());
    }

}
