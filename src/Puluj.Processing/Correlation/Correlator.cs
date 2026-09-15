using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Distance;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;

namespace Puluj.Processing.Correlation;

/// <summary>Breakdown of an association score (stored in TrackTarget.AssociationReason).</summary>
/// <param name="DistanceKm">Between the anchor centres.</param>
/// <param name="GapKm">Between the anchor areas themselves (0 when they overlap or touch): what the object must really have covered.</param>
/// <param name="MaxDistanceKm">How far the areas may lie apart for the object to have covered the gap: speed × time + slack.</param>
public sealed record AssociationScore(double Total, double Time, double Space, double Direction, double Class, double DistanceKm, double GapKm, double MaxDistanceKm, double MinutesApart)
{
    public JsonDocument ToJson() => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        total = Math.Round(Total, 3),
        time = Math.Round(Time, 3),
        space = Math.Round(Space, 3),
        direction = Math.Round(Direction, 3),
        @class = Math.Round(Class, 3),
        distanceKm = Math.Round(DistanceKm, 1),
        gapKm = Math.Round(GapKm, 1),
        maxDistanceKm = Math.Round(MaxDistanceKm, 1),
        minutesApart = Math.Round(MinutesApart, 1),
    }));
}

/// <summary>A track together with its correlation score.</summary>
public sealed record ScoredTrack(TargetTrack Track, AssociationScore Score);

/// <summary>
/// Where an target or a track "is" for correlation purposes. A located report anchors at its place; a report that
/// only names a destination ("1 БпЛА на Конотоп") anchors at the approach to that place. Admin areas carry their polygon,
/// so a town 20 km outside an oblast is not "inside" it just because the oblast's covering radius is 150 km.
/// </summary>
public sealed record SpatialAnchor(Coordinate Center, double AccuracyKm, int? PlaceId, Geometry? Boundary)
{
    /// <summary>Kilometres the object must have covered between two anchors: 0 when the areas overlap or touch.</summary>
    public double GapTo(SpatialAnchor other)
    {
        if (Boundary is null && other.Boundary is null)
        {
            return Math.Max(0, Geo.DistanceKm(Center, other.Center) - AccuracyKm - other.AccuracyKm);
        }
        var a = Boundary ?? Geo.Point(Center.X, Center.Y);
        var b = other.Boundary ?? Geo.Point(other.Center.X, other.Center.Y);
        var nearest = DistanceOp.NearestPoints(a, b);
        var d = Geo.DistanceKm(nearest[0], nearest[1]);
        // Point-like anchors keep their own radius; a polygon is exact.
        return Math.Max(0, d - (Boundary is null ? AccuracyKm : 0) - (other.Boundary is null ? other.AccuracyKm : 0));
    }
}

/// <summary>
/// Pure scoring of "does this target continue that track" (spec §10): type, model/family, time, geography,
/// direction, count. Deterministic and side-effect free so it can be unit-tested on synthetic scenarios.
/// </summary>
public static class Correlator
{
    private const double DefaultSpeedKmh = 200;
    private const double SameSourceSplitMinutes = 8;
    /// <summary>"на Конотоп" puts the object somewhere on the approach to the town, not in it: this wide.</summary>
    public const double DestinationAnchorKm = 40;
    /// <summary>Score cap for pairs that cannot be the same object; the attach threshold is above it.</summary>
    private const double Impossible = 0.3;

    public static bool ClassCompatible(Target o, TargetTrack t)
    {
        if (o.TargetCategoryId is null || o.TargetCategoryId != t.TargetCategoryId)
        {
            return false;
        }
        if (o.TargetClassId is not null && t.TargetClassId is not null && o.TargetClassId != t.TargetClassId)
        {
            return false;
        }
        // A model is a positive identification: two different explicit models must never be joined just because
        // time and location happen to be compatible. A missing model remains intentionally unspecific.
        return o.TargetModelId is null || t.TargetModelId is null || o.TargetModelId == t.TargetModelId;
    }

    /// <summary>
    /// Selects a qualifying candidate only when it is unambiguously better than the runner-up. Ordering by track id
    /// makes diagnostics deterministic, while the strict margin still rejects equal or near-equal scores.
    /// </summary>
    public static ScoredTrack? SelectBestTrack(IEnumerable<ScoredTrack> candidates, double attachThreshold, double ambiguityMargin)
    {
        var ranked = candidates
            .OrderByDescending(candidate => candidate.Score.Total)
            .ThenBy(candidate => candidate.Track.TargetTrackId)
            .ToList();
        if (ranked.Count == 0 || ranked[0].Score.Total < attachThreshold)
        {
            return null;
        }
        if (ranked.Count == 1)
        {
            return ranked[0];
        }
        return ranked[0].Score.Total - ranked[1].Score.Total > Math.Max(0, ambiguityMargin)
            ? ranked[0]
            : null;
    }

    public static SpatialAnchor? AnchorOf(Target o, GazetteerIndex? gazetteer = null)
    {
        if (o.Location is not null)
        {
            return new SpatialAnchor(o.Location.Centroid.Coordinate, o.LocationAccuracyKm ?? 0, o.LocationPlaceId, gazetteer?.Get(o.LocationPlaceId ?? -1)?.Boundary);
        }
        if (o.DestinationPlaceId is int d && gazetteer?.Get(d) is { } dest)
        {
            return new SpatialAnchor(dest.Centroid.Coordinate, Math.Max(dest.RadiusKm, DestinationAnchorKm), d, null);
        }
        return null;
    }

    public static SpatialAnchor? AnchorOf(TargetTrack t, GazetteerIndex? gazetteer = null)
    {
        if (t.LastLocation is null)
        {
            return null;
        }
        // A destination anchor is an approach zone, not the place's polygon.
        var boundary = t.LastLocationKind == LocationKind.DirectionOnly ? null : gazetteer?.Get(t.LastLocationPlaceId ?? -1)?.Boundary;
        return new SpatialAnchor(t.LastLocation.Centroid.Coordinate, t.LastLocationAccuracyKm ?? 0, t.LastLocationPlaceId, boundary);
    }

    public static AssociationScore Score(Target o, TargetTrack t, ClassProfile? profile, double slackKm) =>
        Score(o, t, profile, slackKm, AnchorOf(o), AnchorOf(t));

    public static AssociationScore Score(Target o, TargetTrack t, ClassProfile? profile, double slackKm, SpatialAnchor? oAnchor, SpatialAnchor? tAnchor)
    {
        var windowMin = profile?.CorrelationWindowMinutes ?? 30;
        var minutes = Math.Abs((o.ObservedAt - t.LastSeenAt).TotalMinutes);
        var time = Math.Clamp(1 - minutes / windowMin, 0, 1);
        var speed = profile?.SpeedKmhMax ?? DefaultSpeedKmh;
        var reach = speed * minutes / 60 + slackKm;

        double space, distance = 0, gap = 0;
        var anchored = oAnchor is not null && tAnchor is not null;
        if (anchored)
        {
            distance = Geo.DistanceKm(oAnchor!.Center, tAnchor!.Center);
            gap = oAnchor.GapTo(tAnchor);
            space = gap <= reach ? (reach > 0 ? 1 - 0.5 * gap / reach : 1) : 0;
        }
        else
        {
            space = 0; // one side has no usable location at all: nothing ties the two together
        }

        double direction;
        if (o.DirectionDeg is double od && t.DirectionDeg is double td)
        {
            direction = 1 - Geo.AngleDiffDeg(od, td) / 180;
        }
        else if (anchored && t.DirectionDeg is double td2 && gap > 15)
        {
            // Did the object move roughly where the track was heading?
            var bearing = Geo.BearingDeg(tAnchor!.Center, oAnchor!.Center);
            direction = 1 - Geo.AngleDiffDeg(bearing, td2) / 180;
        }
        else
        {
            direction = 0.5;
        }

        double cls;
        if (o.TargetModelId is not null && o.TargetModelId == t.TargetModelId)
        {
            cls = 1;
        }
        else if (o.TargetModelId is not null && t.TargetModelId is not null)
        {
            cls = 0.2; // both specific and different (Kh-101 vs Kalibr)
        }
        else if (o.TargetFamilyId is not null && o.TargetFamilyId == t.TargetFamilyId)
        {
            cls = 0.9;
        }
        else if (o.TargetFamilyId is not null && t.TargetFamilyId is not null)
        {
            cls = 0.3;
        }
        else
        {
            cls = 0.7; // same class, one side unspecific
        }

        var total = 0.30 * time + 0.35 * space + 0.15 * direction + 0.20 * cls;
        if (space == 0)
        {
            total = Math.Min(total, Impossible); // physically impossible jump, or no location on one side: never attach
        }
        // The same source naming two different places within a few minutes is reporting two objects, not one moving
        // faster than region-level accuracy can tell (typical "БпЛА на Сумщині / БпЛА на Чернігівщині" lists).
        if (anchored && o.SourceId == t.LastSourceId && minutes < SameSourceSplitMinutes
            && oAnchor!.PlaceId is not null && tAnchor!.PlaceId is not null && oAnchor.PlaceId != tAnchor.PlaceId
            && distance > 2 * speed * minutes / 60 + 20)
        {
            total = Math.Min(total, Impossible);
        }
        return new AssociationScore(total, time, space, direction, cls, distance, gap, reach, minutes);
    }
}
