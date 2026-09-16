using System.Text.Json;
using NetTopologySuite.Geometries;
using Puluj.Domain.Entities;
using Puluj.Processing.Correlation;
using Puluj.Processing.Incidents;

namespace Puluj.Processing.Tests.Incidents;

/// <summary>P10 (ADR-0010): the conservative merge policy is pure — every branch on synthetic candidates.</summary>
public class IncidentPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly KindPolicy Explosion = new(120, 5, []);
    private static readonly KindPolicy Confirmed = new(360, 5, ["impact.explosion.reported"]);

    private static SpatialAnchor Kharkiv(double radiusKm = 15) => new(new Coordinate(36.23, 49.99), radiusKm, 3126, null);
    private static SpatialAnchor Sumy() => new(new Coordinate(34.80, 50.91), 12, 1, null);

    private static IncidentCandidate Candidate(long id, DateTimeOffset at, SpatialAnchor? anchor = null, string kind = "impact.explosion.reported", string state = Incident.Reported,
        DateTimeOffset? closedAt = null, int[]? sources = null, int? placeId = 3126, int? regionId = 8) =>
        new(id, kind, state, false, at, closedAt, anchor ?? Kharkiv(), placeId, regionId, (sources ?? [1]).ToHashSet(), (sources ?? [1])[0]);

    private static IncidentFact Fact(DateTimeOffset at, SpatialAnchor? anchor = null, int source = 2, string kind = "impact.explosion.reported", int? placeId = 3126, int? regionId = 8) =>
        new(Guid.NewGuid(), source, kind, at, anchor ?? Kharkiv(), placeId, regionId);

    [Fact]
    public void No_candidates_creates_a_canonical_incident()
    {
        var d = IncidentPolicy.Decide(Fact(T0), [], Explosion);
        Assert.True(d.CreatesIncident);
        Assert.Equal(IncidentObservation.Canonical, d.Relation);
        Assert.Equal(0, d.Reason["considered"]!.GetValue<int>());
        Assert.Equal(IncidentPolicy.Version, d.Reason["policy_version"]!.GetValue<string>());
    }

    [Fact]
    public void Same_place_inside_the_window_attaches_as_supports_from_another_source()
    {
        var d = IncidentPolicy.Decide(Fact(T0.AddMinutes(10)), [Candidate(7, T0)], Explosion);
        Assert.Equal(7, d.IncidentId);
        Assert.Equal(IncidentObservation.Supports, d.Relation);
        Assert.True(d.Score > 0.9, d.Score.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, d.Reason["considered"]!.GetValue<int>());
    }

    [Fact]
    public void The_window_is_symmetric_and_exclusive_past_it()
    {
        Assert.Equal(7, IncidentPolicy.Decide(Fact(T0.AddMinutes(-30)), [Candidate(7, T0)], Explosion).IncidentId); // earlier evidence of the same event (score 0.85)
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(121)), [Candidate(7, T0)], Explosion).CreatesIncident);
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(-121)), [Candidate(7, T0)], Explosion).CreatesIncident);
    }

    [Fact]
    public void Below_the_threshold_is_a_new_incident()
    {
        // 110 of 120 minutes → time 0.083; space 1 → 0.45 < 0.55.
        var d = IncidentPolicy.Decide(Fact(T0.AddMinutes(110)), [Candidate(7, T0)], Explosion);
        Assert.True(d.CreatesIncident);
        Assert.Equal(IncidentObservation.Canonical, d.Relation);
        Assert.Equal(1, d.Reason["considered"]!.GetValue<int>()); // considered, not good enough
    }

    [Fact]
    public void Another_oblast_is_never_a_candidate_a_neighbour_is_reported_as_near()
    {
        var far = IncidentPolicy.Decide(Fact(T0.AddMinutes(5), Sumy(), regionId: 7), [Candidate(7, T0)], Explosion);
        Assert.True(far.CreatesIncident);
        Assert.Null(far.Reason["near_candidates"]);
        // ~40 km between centres, 30 km of radii: 10 km beyond the 5 km slack, inside 3 × slack → near (review), still a new incident.
        var neighbour = new SpatialAnchor(new Coordinate(36.23, 49.99 + 0.36), 15, 999, null);
        var near = IncidentPolicy.Decide(Fact(T0.AddMinutes(5), neighbour, placeId: 999), [Candidate(7, T0, Kharkiv(15))], Explosion);
        Assert.True(near.CreatesIncident);
        Assert.Equal([7L], near.Reason["near_candidates"]!.AsArray().Select(n => n!.GetValue<long>()));
    }

    [Fact]
    public void Containment_an_oblast_report_joins_the_city_incident()
    {
        var oblast = new SpatialAnchor(new Coordinate(36.50, 49.61), 127.5, 8, null);
        var d = IncidentPolicy.Decide(Fact(T0.AddMinutes(10), oblast, placeId: 8), [Candidate(7, T0)], Explosion);
        Assert.Equal(7, d.IncidentId);
        Assert.Equal(0, d.Reason["candidates"]![0]!["gap_km"]!.GetValue<double>());
    }

    [Fact]
    public void Without_geometry_only_the_very_same_place_counts()
    {
        Assert.Equal(7, IncidentPolicy.Decide(Fact(T0.AddMinutes(1), placeId: 3126) with { Anchor = null }, [Candidate(7, T0)], Explosion).IncidentId);
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(1), placeId: 3127) with { Anchor = null }, [Candidate(7, T0)], Explosion).CreatesIncident);
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(1), placeId: null) with { Anchor = null }, [Candidate(7, T0)], Explosion).CreatesIncident);
    }

    [Fact]
    public void Echo_supports_and_confirms()
    {
        var incident = Candidate(7, T0, sources: [1]);
        Assert.Equal(IncidentObservation.Echo, IncidentPolicy.Relation(Fact(T0, source: 1), incident, Explosion));
        Assert.Equal(IncidentObservation.Supports, IncidentPolicy.Relation(Fact(T0, source: 2), incident, Explosion));
        // A confirming kind from another source confirms; from the canonical source it is only an echo.
        Assert.Equal(IncidentObservation.Confirms, IncidentPolicy.Relation(Fact(T0, source: 2, kind: "impact.confirmed"), incident, Confirmed));
        Assert.Equal(IncidentObservation.Echo, IncidentPolicy.Relation(Fact(T0, source: 1, kind: "impact.confirmed"), incident, Confirmed));
        // No canonical source (an ambiguous or split-founded incident): a channel already on the incident still cannot confirm it (B4).
        var noCanonical = incident with { CanonicalSourceId = null, SourceIds = new HashSet<int> { 1, 3 } };
        Assert.Equal(IncidentObservation.Echo, IncidentPolicy.Relation(Fact(T0, source: 3, kind: "impact.confirmed"), noCanonical, Confirmed));
        Assert.Equal(IncidentObservation.Confirms, IncidentPolicy.Relation(Fact(T0, source: 2, kind: "impact.confirmed"), noCanonical, Confirmed));
        Assert.Equal(Incident.Confirmed, IncidentPolicy.NextState(Incident.Reported, IncidentObservation.Confirms));
        Assert.Equal(Incident.Reported, IncidentPolicy.NextState(Incident.Reported, IncidentObservation.Supports));
        Assert.Equal(Incident.Resolved, IncidentPolicy.NextState(Incident.Resolved, IncidentObservation.Confirms));
    }

    [Fact]
    public void Kinds_never_cross_over_unless_the_fact_confirms_the_candidate()
    {
        var explosion = Candidate(7, T0);
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(1), kind: "fire.reported"), [explosion], new KindPolicy(360, 5, [])).CreatesIncident);
        var confirmed = IncidentPolicy.Decide(Fact(T0.AddMinutes(1), kind: "impact.confirmed"), [explosion], Confirmed);
        Assert.Equal(7, confirmed.IncidentId);
        Assert.Equal(IncidentObservation.Confirms, confirmed.Relation);
        // The reverse: an explosion report never joins a confirmed-hit incident (its policy does not confirm that kind).
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(1)), [Candidate(8, T0, kind: "impact.confirmed")], Explosion).CreatesIncident);
    }

    [Fact]
    public void Closed_incidents_take_only_late_evidence()
    {
        var resolved = Candidate(7, T0, state: Incident.Resolved, closedAt: T0.AddMinutes(30));
        Assert.Equal(7, IncidentPolicy.Decide(Fact(T0.AddMinutes(20)), [resolved], Explosion).IncidentId); // before the closure: late evidence
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(31)), [resolved], Explosion).CreatesIncident); // after it: a new event
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(20)), [Candidate(7, T0, state: Incident.Resolved, closedAt: null)], Explosion).CreatesIncident); // unknown closure: never
        Assert.True(IncidentPolicy.Decide(Fact(T0.AddMinutes(20)), [Candidate(7, T0, state: Incident.Retracted, closedAt: T0.AddMinutes(30))], Explosion).CreatesIncident); // retracted (hoax, merged): never, even late
    }

    [Fact]
    public void Two_equally_plausible_candidates_are_ambiguous_a_clear_winner_is_not()
    {
        var a = Candidate(7, T0);
        var b = Candidate(8, T0.AddMinutes(20));
        var tie = IncidentPolicy.Decide(Fact(T0.AddMinutes(10)), [a, b], Explosion);
        Assert.True(tie.CreatesIncident);
        Assert.Equal(IncidentObservation.Ambiguous, tie.Relation);
        Assert.Equal([7L, 8L], tie.Reason["ambiguous"]!.AsArray().Select(n => n!.GetValue<long>()));
        var clear = IncidentPolicy.Decide(Fact(T0.AddMinutes(2)), [a, Candidate(8, T0.AddMinutes(60))], Explosion);
        Assert.Equal(7, clear.IncidentId);
        Assert.Equal(2, clear.Reason["candidates"]!.AsArray().Count);
    }

    [Fact]
    public void Kind_policy_reads_the_seed_and_falls_back_to_defaults()
    {
        Assert.Equal(KindPolicy.Default, KindPolicy.From(null));
        Assert.Equal(KindPolicy.Default, KindPolicy.From(new EventKind { Code = "x", NameUk = "x", DedupPolicy = null }));
        var p = KindPolicy.From(new EventKind { Code = "x", NameUk = "x", DedupPolicy = JsonDocument.Parse("""{"windowMinutes": 360, "slackKm": 2.5, "confirms": ["impact.explosion.reported"]}""") });
        Assert.Equal(360, p.WindowMinutes);
        Assert.Equal(2.5, p.SlackKm);
        Assert.Equal(["impact.explosion.reported"], p.Confirms);
        Assert.Equal(1, KindPolicy.From(new EventKind { Code = "x", NameUk = "x", DedupPolicy = JsonDocument.Parse("""{"windowMinutes": -5}""") }).WindowMinutes);
    }
}
