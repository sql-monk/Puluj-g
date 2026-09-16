using System.Text.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Puluj.Api;
using Puluj.Api.Hubs;
using Puluj.Api.Services;
using Puluj.Contracts;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Tests;

/// <summary>P11 (ADR-0011): precision from the evidence kind, the keyset cursor, the window/page validation, the push adapter and the additive wire contract.</summary>
public class IncidentReadSideTests
{
    private static readonly ReferenceCache.PlaceInfo Kharkiv = new(3126, "Харків", PlaceLevel.City, 8, "UA", 36.23, 49.99, 15, 1_400_000);
    private static readonly ReferenceCache.PlaceInfo Oblast = new(8, "Харківська область", PlaceLevel.Region, null, "UA", 36.5, 49.6, 127.5, 0);
    private static readonly ReferenceCache.PlaceInfo Raion = new(500, "Ізюмський район", PlaceLevel.District, 8, "UA", 37.2, 49.2, 40, 0);

    [Fact]
    public void R01_Precision_comes_from_the_evidence_kind_never_from_a_small_radius()
    {
        Assert.Equal("city", IncidentQueries.Precision(LocationKind.City, 15, Kharkiv));
        Assert.Equal("city", IncidentQueries.Precision(LocationKind.City, 2, Kharkiv)); // a city centroid with a small radius is still a city marker
        Assert.Equal("region", IncidentQueries.Precision(LocationKind.Region, 127.5, Oblast));
        Assert.Equal("district", IncidentQueries.Precision(LocationKind.District, 40, Raion));
        Assert.Equal("region", IncidentQueries.Precision(LocationKind.Area, 60, null));
        Assert.Equal("point", IncidentQueries.Precision(LocationKind.Point, 0.5, Kharkiv)); // only a fact the parser located as a point
        Assert.Equal("unknown", IncidentQueries.Precision(LocationKind.DirectionOnly, null, null));
        Assert.Equal("unknown", IncidentQueries.Precision(LocationKind.Unknown, null, null));
        // A row without a kind but with a place (older data): the place level decides.
        Assert.Equal("city", IncidentQueries.Precision(LocationKind.Unknown, 15, Kharkiv));
        Assert.Equal("region", IncidentQueries.Precision(LocationKind.Unknown, 127.5, Oblast));
    }

    [Fact]
    public void R02_Cursor_round_trips_and_the_window_is_validated()
    {
        var at = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var cursor = IncidentQueries.EncodeCursor(at, 42);
        Assert.Equal((at, 42L), IncidentQueries.DecodeCursor(cursor));
        Assert.Null(IncidentQueries.DecodeCursor(null));
        Assert.Throws<IncidentQueries.QueryException>(() => IncidentQueries.DecodeCursor("not-base64!"));
        Assert.Throws<IncidentQueries.QueryException>(() => IncidentQueries.DecodeCursor(Convert.ToBase64String("junk"u8.ToArray())));

        var clock = new FixedClock(at);
        var queries = new IncidentQueries(null!, null!, clock, Options.Create(new MapOptions { IncidentHours = 24, IncidentMaxWindowDays = 7 }));
        var defaults = queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, null, null, null, false));
        Assert.Equal(at, defaults.To);
        Assert.Equal(at.AddHours(-24), defaults.From);
        Assert.Equal(IncidentQueries.DefaultPageSize, defaults.Limit);
        Assert.Equal(IncidentQueries.EffectiveMode, defaults.Mode);
        Assert.Equal(["confirmed", "reported", "resolved"], defaults.States.Order()); // retracted is never in by default
        Assert.Equal(IncidentQueries.MaxPageSize, queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, 5000, null, null, false)).Limit);
        Assert.Equal(1, queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, -3, null, null, false)).Limit);
        Assert.Contains("7 days", Assert.Throws<IncidentQueries.QueryException>(() => queries.Validate(new IncidentQueries.Query(at.AddDays(-8), at, null, null, null, null, null, null, null, false))).Message);
        Assert.Throws<IncidentQueries.QueryException>(() => queries.Validate(new IncidentQueries.Query(at, at.AddHours(-1), null, null, null, null, null, null, null, false)));
        Assert.Throws<IncidentQueries.QueryException>(() => queries.Validate(new IncidentQueries.Query(null, null, "reported,bogus", null, null, null, null, null, null, false)));
        Assert.Throws<IncidentQueries.QueryException>(() => queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, null, "recorded", null, false)));
        var recorded = queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, null, "recorded", at.AddHours(-3), false));
        Assert.Equal(at.AddHours(-3), recorded.To); // the window ends at asOf unless asked otherwise
        Assert.Equal(at.AddHours(-27), recorded.From);
        Assert.Throws<IncidentQueries.QueryException>(() => queries.Validate(new IncidentQueries.Query(null, null, null, null, null, null, null, "future", null, false)));
        Assert.Equal("system", IncidentQueries.RedactActor("incident-worker@worker-1"));
        Assert.Equal("operator", IncidentQueries.RedactActor("ops"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeClient : IMapClient
    {
        public readonly List<string> Calls = [];
        public Task TrackUpserted(TrackDto track) => Record("TrackUpserted");
        public Task TrackClosed(TrackDto track) => Record("TrackClosed");
        public Task AlertChanged(AlertDto alert) => Record("AlertChanged");
        public Task TargetCreated(TargetDto target) => Record("TargetCreated");
        public Task IncidentUpserted(IncidentDto incident) => Record($"IncidentUpserted:{incident.Id}:{incident.Revision}");
        public Task IncidentRevised(IncidentDto incident) => Record($"IncidentRevised:{incident.Id}:{incident.Revision}");
        public Task Resync(DateTimeOffset at) => Record("Resync");
        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    private static IncidentDto Incident(long id, int revision, string state = "reported", bool suppressed = false) => new(id, "impact.explosion.reported", "Вибухи", "incident", state, suppressed,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        new IncidentLocationDto("city", 3126, "Харків", 8, "Харківська область", Geo.Point(36.23, 49.99), 15, "city"),
        "medium", 1, revision, null, null, new IncidentProvenanceDto(Guid.NewGuid(), 1, [1], "incident-1/p2", Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public async Task R06_Push_adapter_maps_the_first_revision_to_upserted_and_everything_else_to_revised()
    {
        var client = new FakeClient();
        await IncidentPush.SendAsync(client, Incident(1, 1), 1);
        await IncidentPush.SendAsync(client, Incident(1, 2), 2); // a link, a confirmation
        await IncidentPush.SendAsync(client, Incident(1, 3, "resolved"), 3);
        await IncidentPush.SendAsync(client, Incident(1, 4, suppressed: true), 4);
        await IncidentPush.SendAsync(client, Incident(2, 3), 1); // announced as the first revision, but the row is already newer: the client gets the newest state as a revision
        await IncidentPush.SendAsync(client, Incident(3, 1), null); // an older worker's NOTIFY without a revision: the row decides
        Assert.Equal(["IncidentUpserted:1:1", "IncidentRevised:1:2", "IncidentRevised:1:3", "IncidentRevised:1:4", "IncidentRevised:2:3", "IncidentUpserted:3:1"], client.Calls);
    }

    [Fact]
    public void R07_Wire_contract_is_additive_camel_case_geo_json()
    {
        var options = new JsonSerializerOptions();
        ApiDependencyInjection.ConfigureJson(options);
        var snapshot = new SnapshotDto(DateTimeOffset.UnixEpoch, false, [], [], [], [Incident(7, 2)], true);
        var json = JsonSerializer.Serialize(snapshot, options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("incidents", out var incidents)); // additive: an old client ignores it
        Assert.True(root.GetProperty("incidentsTruncated").GetBoolean());
        var incident = incidents[0];
        Assert.Equal(7, incident.GetProperty("id").GetInt64());
        Assert.Equal("impact.explosion.reported", incident.GetProperty("kind").GetString());
        Assert.Equal(2, incident.GetProperty("revision").GetInt32());
        Assert.Equal("city", incident.GetProperty("location").GetProperty("precision").GetString());
        Assert.Equal("Point", incident.GetProperty("location").GetProperty("point").GetProperty("type").GetString()); // GeoJSON, like every other DTO
        Assert.Equal(36.23, incident.GetProperty("location").GetProperty("point").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal("incident-1/p2", incident.GetProperty("provenance").GetProperty("policyVersion").GetString());
        Assert.False(incident.TryGetProperty("closureReason", out _)); // nulls are omitted
        Assert.False(incident.TryGetProperty("mergedIntoIncidentId", out _));
        // Without incidents the old shape is byte-for-byte what it was.
        var legacy = JsonSerializer.Serialize(new SnapshotDto(DateTimeOffset.UnixEpoch, false, [], [], []), options);
        Assert.DoesNotContain("incidents", legacy);
        // The hub contract: every method the client subscribes to exists with one DTO argument.
        Assert.Equal(["AlertChanged", "IncidentRevised", "IncidentUpserted", "Resync", "TargetCreated", "TrackClosed", "TrackUpserted"], typeof(IMapClient).GetMethods().Select(m => m.Name).Order());
        Assert.All(typeof(IMapClient).GetMethods(), m => Assert.Single(m.GetParameters()));
    }
}
