using System.Text.Json;
using System.Text.Json.Nodes;
using Puluj.Domain.Entities;
using Puluj.Processing.Correlation;

namespace Puluj.Processing.Incidents;

/// <summary>Per-kind candidate policy, read from <c>event_kinds.dedup_policy</c> (seed policyVersion ≥ 2); code defaults otherwise.</summary>
public sealed record KindPolicy(int WindowMinutes, double SlackKm, IReadOnlyList<string> Confirms)
{
    public static readonly KindPolicy Default = new(120, 5, []);

    public static KindPolicy From(EventKind? kind)
    {
        if (kind?.DedupPolicy is not { } doc || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }
        var root = doc.RootElement;
        var window = root.TryGetProperty("windowMinutes", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : Default.WindowMinutes;
        var slack = root.TryGetProperty("slackKm", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : Default.SlackKm;
        var confirms = root.TryGetProperty("confirms", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
        return new KindPolicy(Math.Max(1, window), Math.Max(0, slack), confirms);
    }
}

/// <summary>An incident as the policy sees it (loaded under the kind lock).</summary>
public sealed record IncidentCandidate(long IncidentId, string KindCode, string State, bool Suppressed, DateTimeOffset EventAt, DateTimeOffset? ClosureEffectiveAt,
    SpatialAnchor? Anchor, int? PlaceId, int? RegionPlaceId, IReadOnlySet<int> SourceIds, int? CanonicalSourceId);

/// <summary>The new observation as the policy sees it.</summary>
public sealed record IncidentFact(Guid ObservationId, int SourceId, string KindCode, DateTimeOffset EffectiveAt, SpatialAnchor? Anchor, int? PlaceId, int? RegionPlaceId);

/// <summary>Where the observation goes and why: an existing incident (with the relation) or a new one; the reason is stored on the link.</summary>
public sealed record IncidentDecision(long? IncidentId, string Relation, double Score, JsonObject Reason)
{
    public bool CreatesIncident => IncidentId is null;
}

/// <summary>
/// Plan §8.4 conservative merge policy (P10, policy `incident-1`): pure and deterministic. A candidate is an incident of
/// the fact's kind (or of a kind the fact's kind confirms) inside the symmetric time window and spatially compatible
/// (polygon/radius gap ≤ slack, via <see cref="SpatialAnchor.GapTo"/>). Score = 0.6·time + 0.4·space; attach at ≥
/// <see cref="Threshold"/>; two candidates closer than <see cref="AmbiguityMargin"/> → a new incident marked ambiguous
/// (review), never a guess. Facts without a usable location attach only by the very same place id. Closed incidents take
/// a fact only when it is older than their closure (late evidence), without reopening.
/// </summary>
public static class IncidentPolicy
{
    public const string Version = "incident-1";
    public const double Threshold = 0.55;
    public const double AmbiguityMargin = 0.1;
    public const double TimeWeight = 0.6;
    public const double SpaceWeight = 0.4;
    /// <summary>Candidates outside the spatial slack but within this many slacks are reported as near (review), not merged.</summary>
    public const double NearFactor = 3;

    public static IncidentDecision Decide(IncidentFact fact, IReadOnlyList<IncidentCandidate> candidates, KindPolicy policy)
    {
        var window = TimeSpan.FromMinutes(policy.WindowMinutes);
        var scored = new List<(IncidentCandidate Candidate, double Score, double Gap, double Dt)>();
        var near = new List<long>();
        var considered = 0;
        foreach (var c in candidates)
        {
            if (c.KindCode != fact.KindCode && !policy.Confirms.Contains(c.KindCode, StringComparer.Ordinal))
            {
                continue; // crossover between kinds is never merged (ADR-0010)
            }
            var dt = Math.Abs((fact.EffectiveAt - c.EventAt).TotalMinutes);
            if (dt > policy.WindowMinutes)
            {
                continue;
            }
            if (c.State is Incident.Resolved or Incident.Retracted && (c.ClosureEffectiveAt is null || fact.EffectiveAt > c.ClosureEffectiveAt))
            {
                continue; // a report after the closure is a new event, never a reopen
            }
            considered++;
            var time = 1 - dt / policy.WindowMinutes;
            double space;
            double gap;
            if (fact.Anchor is null || c.Anchor is null)
            {
                // No usable geometry on one side: only the very same place counts (§8.5: never invent proximity).
                if (fact.PlaceId is null || fact.PlaceId != c.PlaceId)
                {
                    continue;
                }
                space = 1;
                gap = 0;
            }
            else
            {
                gap = fact.Anchor.GapTo(c.Anchor);
                if (gap > policy.SlackKm)
                {
                    if (gap <= policy.SlackKm * NearFactor || (fact.RegionPlaceId is not null && fact.RegionPlaceId == c.RegionPlaceId))
                    {
                        near.Add(c.IncidentId); // a border event in the next district, or the same oblast: for the reviewer, not for the merge
                    }
                    continue;
                }
                space = policy.SlackKm == 0 ? 1 : 1 - gap / policy.SlackKm;
            }
            scored.Add((c, TimeWeight * time + SpaceWeight * space, gap, dt));
        }
        var ordered = scored.Where(s => s.Score >= Threshold).OrderByDescending(s => s.Score).ThenBy(s => s.Candidate.IncidentId).ToList();
        var reason = new JsonObject
        {
            ["policy_version"] = Version,
            ["window_minutes"] = policy.WindowMinutes,
            ["slack_km"] = policy.SlackKm,
            ["considered"] = considered,
            ["candidates"] = new JsonArray(ordered.Take(5).Select(s => (JsonNode)new JsonObject
            {
                ["incident_id"] = s.Candidate.IncidentId,
                ["score"] = Math.Round(s.Score, 3),
                ["gap_km"] = Math.Round(s.Gap, 2),
                ["dt_minutes"] = Math.Round(s.Dt, 1),
            }).ToArray()),
        };
        if (near.Count > 0)
        {
            reason["near_candidates"] = new JsonArray(near.Distinct().Select(id => (JsonNode)id).ToArray());
        }
        if (ordered.Count == 0)
        {
            return new IncidentDecision(null, IncidentObservation.Canonical, 1, reason);
        }
        if (ordered.Count > 1 && ordered[0].Score - ordered[1].Score < AmbiguityMargin)
        {
            reason["ambiguous"] = new JsonArray(ordered.Take(2).Select(s => (JsonNode)s.Candidate.IncidentId).ToArray());
            return new IncidentDecision(null, IncidentObservation.Ambiguous, ordered[0].Score, reason); // two plausible events: separate, flagged for review
        }
        var best = ordered[0];
        var relation = Relation(fact, best.Candidate, policy);
        reason["relation"] = relation;
        return new IncidentDecision(best.Candidate.IncidentId, relation, best.Score, reason);
    }

    /// <summary>`confirms` only for a confirming kind from another source; the same source again is an `echo` (never raises the state); otherwise `supports`.</summary>
    public static string Relation(IncidentFact fact, IncidentCandidate incident, KindPolicy policy)
    {
        if (policy.Confirms.Contains(incident.KindCode, StringComparer.Ordinal) && fact.KindCode != incident.KindCode && fact.SourceId != incident.CanonicalSourceId)
        {
            return IncidentObservation.Confirms;
        }
        return incident.SourceIds.Contains(fact.SourceId) ? IncidentObservation.Echo : IncidentObservation.Supports;
    }

    /// <summary>State after a link: only a `confirms` relation moves reported → confirmed; nothing else changes the state automatically.</summary>
    public static string NextState(string current, string relation) =>
        current == Incident.Reported && relation == IncidentObservation.Confirms ? Incident.Confirmed : current;
}
