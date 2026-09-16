using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Puluj.Admin.Endpoints;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Incidents;

namespace Puluj.Admin.Tests;

/// <summary>P12 (§8.7): the catalog editor's validation (400/422), the legacy-mapping guard, the audit snapshot, and the merge plan shared with the incident command.</summary>
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
        Assert.False(snapshot.TryGetProperty("dedupPolicy", out _)); // seed-owned: never in the admin audit
    }

    private static Incident Inc(long id, int kind, string state = Incident.Reported, Guid? generation = null, params (int Source, DateTimeOffset At)[] links)
    {
        var i = new Incident { IncidentId = id, EventKindId = kind, State = state, GenerationId = generation ?? Guid.Empty, FirstReportedAt = DateTimeOffset.UnixEpoch, LastReportedAt = DateTimeOffset.UnixEpoch, Revision = 1, Confidence = ConfidenceLevel.Medium, AccuracyKm = 15, Geometry = Puluj.Infrastructure.Persistence.Geo.Point(36.23, 49.99) };
        foreach (var (source, at) in links)
        {
            i.Observations.Add(new IncidentObservation { IncidentId = id, ObservationId = Guid.NewGuid(), SourceId = source, Relation = IncidentObservation.Supports, EffectiveAt = at, LinkedAt = at, PolicyVersion = "t" });
        }
        return i;
    }

    [Fact]
    public void Merge_plan_applies_the_command_rules_and_predicts_the_aggregate()
    {
        var t0 = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var source = Inc(1, 5, links: [(1, t0), (2, t0.AddMinutes(5))]);
        var target = Inc(2, 5, Incident.Confirmed, links: [(2, t0.AddMinutes(-10)), (3, t0.AddMinutes(20))]);
        target.Revision = 4;
        source.AccuracyKm = 3; // the more precise location wins
        source.Confidence = ConfidenceLevel.High;
        var plan = IncidentStateWriter.PlanMerge(source, target);
        Assert.True(plan.Allowed);
        Assert.Equal(2, plan.MovedObservationIds.Count);
        Assert.Equal(3, plan.SourceCountAfter); // sources 1, 2, 3
        Assert.Equal(t0.AddMinutes(-10), plan.FirstReportedAtAfter);
        Assert.Equal(t0.AddMinutes(20), plan.LastReportedAtAfter);
        Assert.Equal(Incident.Confirmed, plan.StateAfter); // the target's state never changes on merge
        Assert.Equal("source", plan.LocationFrom);
        Assert.Equal(3, plan.AccuracyKmAfter);
        Assert.Equal(ConfidenceLevel.High, plan.ConfidenceAfter);
        Assert.Equal(4, plan.TargetRevision);

        Assert.Equal("an incident cannot be merged into itself", IncidentStateWriter.PlanMerge(source, source).Refusal);
        Assert.Contains("different kinds", IncidentStateWriter.PlanMerge(source, Inc(3, 7)).Refusal);
        Assert.Contains("retracted", IncidentStateWriter.PlanMerge(source, Inc(3, 5, Incident.Retracted)).Refusal);
        Assert.Contains("generations", IncidentStateWriter.PlanMerge(source, Inc(3, 5, generation: Guid.NewGuid())).Refusal);
    }
}
