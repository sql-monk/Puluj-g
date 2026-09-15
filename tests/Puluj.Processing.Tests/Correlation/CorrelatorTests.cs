using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Correlation;
using Puluj.Processing.Indexes;

namespace Puluj.Processing.Tests.Correlation;

public class CorrelatorTests
{
    private static readonly ClassProfile Shahed = new(1, "STRIKE_UAV", 150, 200, true, "uav", 20, 60);
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    // Sumy (34.8, 50.9) -> Poltava (34.55, 49.59) is ~150 km south, ~50 min at 180 km/h.
    private static Target Obs(double lon, double lat, int minutes, int? model = null, double? dir = null, int? place = null) => new()
    {
        ObservedAt = T0.AddMinutes(minutes),
        TargetCategoryId = 1,
        TargetClassId = 1,
        TargetFamilyId = 1,
        TargetModelId = model,
        Location = Geo.Point(lon, lat),
        LocationAccuracyKm = 40,
        LocationKind = LocationKind.Region,
        LocationPlaceId = place,
        DirectionDeg = dir,
        DirectionKind = dir is null ? DirectionKind.Unknown : DirectionKind.Compass,
        DirectionConfidence = dir is null ? ConfidenceLevel.Unknown : ConfidenceLevel.High,
        Confidence = ConfidenceLevel.High,
        EventType = EventType.TargetObserved,
    };

    [Fact]
    public void Plausible_continuation_scores_above_threshold()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0, dir: 180), T0);
        var next = Obs(34.55, 49.59, 50, dir: 180);
        var score = Correlator.Score(next, track, Shahed, 30);
        Assert.True(score.Total >= 0.6, $"score {score.Total:F2}: {score}");
    }

    [Fact]
    public void Impossible_jump_never_attaches()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0), T0);
        var lviv = Obs(24.0, 49.84, 10); // ~800 km in 10 minutes
        var score = Correlator.Score(lviv, track, Shahed, 30);
        Assert.True(score.Total < 0.6, $"score {score.Total:F2}");
        Assert.Equal(0, score.Space);
    }

    [Fact]
    public void Different_specific_models_are_penalised()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0, model: 10), T0);
        var other = Obs(34.8, 50.9, 5, model: 11);
        var score = Correlator.Score(other, track, Shahed, 30);
        Assert.Equal(0.2, score.Class);
    }

    [Fact]
    public void Different_explicit_models_are_not_compatible()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0, model: 10), T0);
        var other = Obs(34.8, 50.9, 5, model: 11);

        Assert.False(Correlator.ClassCompatible(other, track));
        other.TargetModelId = null;
        Assert.True(Correlator.ClassCompatible(other, track));
    }

    [Fact]
    public void Unique_best_candidate_is_selected()
    {
        var best = new TargetTrack { TargetTrackId = 10 };
        var runnerUp = new TargetTrack { TargetTrackId = 20 };

        var selected = Correlator.SelectBestTrack(
            [new ScoredTrack(best, Score(0.82)), new ScoredTrack(runnerUp, Score(0.70))], 0.6, 0.05);

        Assert.Same(best, selected?.Track);
    }

    [Fact]
    public void Ambiguous_candidates_are_not_selected()
    {
        var first = new TargetTrack { TargetTrackId = 10 };
        var second = new TargetTrack { TargetTrackId = 20 };

        var selected = Correlator.SelectBestTrack(
            [new ScoredTrack(first, Score(0.82)), new ScoredTrack(second, Score(0.78))], 0.6, 0.05);

        Assert.Null(selected);
    }

    [Fact]
    public void Near_threshold_runner_up_still_makes_the_choice_ambiguous()
    {
        var first = new TargetTrack { TargetTrackId = 10 };
        var second = new TargetTrack { TargetTrackId = 20 };

        var selected = Correlator.SelectBestTrack(
            [new ScoredTrack(first, Score(0.62)), new ScoredTrack(second, Score(0.59))], 0.6, 0.05);

        Assert.Null(selected);
    }

    [Fact]
    public void Opposite_direction_lowers_score()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0, dir: 180), T0);
        var same = Correlator.Score(Obs(34.7, 50.5, 15, dir: 180), track, Shahed, 30);
        var opposite = Correlator.Score(Obs(34.7, 50.5, 15, dir: 0), track, Shahed, 30);
        Assert.True(same.Total > opposite.Total);
    }

    [Fact]
    public void Track_adopts_more_specific_model_and_builds_geometry()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0), T0);
        Assert.Null(track.TrackGeometry);
        TrackUpdater.Apply(track, Obs(34.55, 49.59, 50, model: 10), T0.AddMinutes(50), isNewer: true);
        Assert.Equal(10, track.TargetModelId);
        Assert.NotNull(track.TrackGeometry);
        Assert.Equal(2, track.TrackGeometry!.NumPoints);
        Assert.Equal(2, track.TargetCount);
        // No reported direction: derived from movement, marked as low confidence.
        Assert.Equal(DirectionKind.TowardsPlace, track.DirectionKind);
        Assert.Equal(ConfidenceLevel.Low, track.DirectionConfidence);
        Assert.InRange(track.DirectionDeg!.Value, 170, 200);
    }

    [Fact]
    public void Older_target_does_not_move_the_marker()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.55, 49.59, 50), T0);
        var last = track.LastLocation;
        TrackUpdater.Apply(track, Obs(34.8, 50.9, 0), T0, isNewer: false);
        Assert.Same(last, track.LastLocation);
        Assert.Equal(T0, track.FirstSeenAt);
        Assert.Equal(2, track.TargetCount);
    }

    [Fact]
    public void Track_confidence_grows_with_corroboration()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0), T0);
        track.DistinctSourceCount = 1;
        Assert.Equal(ConfidenceLevel.Medium, TrackUpdater.ComputeTrackConfidence(track, ConfidenceLevel.Medium));
        track.DistinctSourceCount = 2;
        Assert.Equal(ConfidenceLevel.High, TrackUpdater.ComputeTrackConfidence(track, ConfidenceLevel.Medium));
        Assert.Equal(ConfidenceLevel.High, TrackUpdater.ComputeTrackConfidence(track, ConfidenceLevel.High));
    }

    [Fact]
    public void Same_source_reporting_two_regions_minutes_apart_means_two_objects()
    {
        // Sumy region and Chernihiv region centroids are ~150 km apart; nothing covers that in 2 minutes.
        var first = Obs(32.0, 51.4, 0, place: 1);
        first.SourceId = 7;
        first.LocationAccuracyKm = 150; // region-level
        var track = TrackUpdater.CreateTrack(first, T0);
        var second = Obs(34.2, 51.0, 2, place: 2);
        second.SourceId = 7;
        second.LocationAccuracyKm = 150;
        Assert.True(Correlator.Score(second, track, Shahed, 30).Total < 0.6);
        // A different source saying the same thing is corroboration, not a split.
        second.SourceId = 8;
        Assert.True(Correlator.Score(second, track, Shahed, 30).Total >= 0.6);
    }

    [Fact]
    public void Repeated_origin_does_not_drag_the_track_back()
    {
        // Track already in Kyiv oblast; a new message only says "from Chernihiv oblast towards Kyiv".
        var track = TrackUpdater.CreateTrack(Obs(30.45, 50.30, 0, place: 5), T0);
        var next = Obs(32.0, 51.35, 10, place: 6, dir: 225);
        next.OriginPlaceId = 6;
        next.LocationAccuracyKm = 148; // a whole oblast
        TrackUpdater.Apply(track, next, T0.AddMinutes(10), isNewer: true);
        Assert.Equal(5, track.LastLocationPlaceId);
        Assert.Null(track.TrackGeometry);
        Assert.Equal(225, track.DirectionDeg); // the reported course still applies
    }

    [Fact]
    public void Precise_origin_is_a_position_and_moves_the_track()
    {
        // "з Броварів курсом на Київ": Brovary is a town, so the drone is there now.
        var track = TrackUpdater.CreateTrack(Obs(30.45, 50.30, 0, place: 5), T0);
        var next = Obs(30.79, 50.51, 10, place: 3932, dir: 248);
        next.OriginPlaceId = 3932;
        next.LocationAccuracyKm = 3;
        TrackUpdater.Apply(track, next, T0.AddMinutes(10), isNewer: true);
        Assert.Equal(3932, track.LastLocationPlaceId);
        Assert.NotNull(track.TrackGeometry);
    }

    [Fact]
    public void Adjacent_oblasts_give_neither_a_path_nor_a_direction()
    {
        // Sumy oblast then Poltava oblast: the areas overlap, so a line between their centres is not a route anyone flew.
        var first = Obs(34.8, 50.9, 0);
        first.LocationAccuracyKm = 147;
        var track = TrackUpdater.CreateTrack(first, T0);
        var next = Obs(34.55, 49.59, 30);
        next.LocationAccuracyKm = 135;
        TrackUpdater.Apply(track, next, T0.AddMinutes(30), isNewer: true);
        Assert.Null(track.TrackGeometry);
        Assert.Null(track.DirectionDeg);
    }

    [Fact]
    public void Coarse_previous_position_does_not_seed_the_path()
    {
        // "на Сумщині", then a fix at a town: the line must not start at the oblast's centre.
        var first = Obs(34.8, 50.9, 0);
        first.LocationAccuracyKm = 147;
        var track = TrackUpdater.CreateTrack(first, T0);
        var town = Obs(34.55, 50.7, 10);
        town.LocationAccuracyKm = 3;
        TrackUpdater.Apply(track, town, T0.AddMinutes(10), isNewer: true);
        Assert.Null(track.TrackGeometry);
        var next = Obs(34.4, 50.5, 20);
        next.LocationAccuracyKm = 3;
        TrackUpdater.Apply(track, next, T0.AddMinutes(20), isNewer: true);
        Assert.Equal(2, track.TrackGeometry!.NumPoints);
        Assert.Equal(34.55, track.TrackGeometry.Coordinates[0].X, 3);
    }

    // A tiny gazetteer: Kyiv oblast as a rectangle, Brovary inside it, Sumy far to the east.
    private static readonly PlaceEntry KyivOblast = new(5, "Київська область", PlaceLevel.Region, null, "UA", 0, Geo.Point(30.45, 50.30), 146,
        Geo.Factory.CreatePolygon([new(29.3, 49.2), new(32.2, 49.2), new(32.2, 51.5), new(29.3, 51.5), new(29.3, 49.2)]));
    private static readonly PlaceEntry Brovary = new(3932, "Бровари", PlaceLevel.City, 5, "UA", 100_000, Geo.Point(30.79, 50.51), 3);
    private static readonly PlaceEntry Sumy = new(7, "Суми", PlaceLevel.City, null, "UA", 250_000, Geo.Point(34.8, 50.9), 5);
    private static readonly GazetteerIndex Gazetteer = new([(KyivOblast, ["київщин"]), (Brovary, ["бровар"]), (Sumy, ["сум"])]);

    private static Target DestinationOnly(int destination, int minutes) => new()
    {
        ObservedAt = T0.AddMinutes(minutes),
        TargetCategoryId = 1,
        TargetClassId = 1,
        TargetFamilyId = 1,
        LocationKind = LocationKind.DirectionOnly,
        DestinationPlaceId = destination,
        DirectionKind = DirectionKind.Unknown,
        Confidence = ConfidenceLevel.Medium,
        EventType = EventType.TargetObserved,
    };

    private static TargetTrack KyivOblastTrack()
    {
        var first = Obs(30.45, 50.30, 0, place: 5);
        first.LocationAccuracyKm = 146;
        return TrackUpdater.CreateTrack(first, T0);
    }

    private static AssociationScore Score(double total) => new(total, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Destination_only_report_is_anchored_on_the_approach_to_the_place()
    {
        // "1 БпЛА на Бровари" continues a Kyiv-oblast track; "1 БпЛА на Суми" (250 km east of the oblast) does not,
        // even though the oblast's covering radius of 146 km would have said "close enough".
        var track = KyivOblastTrack();
        var tAnchor = Correlator.AnchorOf(track, Gazetteer);
        Assert.NotNull(tAnchor!.Boundary);
        var brovary = Correlator.Score(DestinationOnly(3932, 3), track, Shahed, 30, Correlator.AnchorOf(DestinationOnly(3932, 3), Gazetteer), tAnchor);
        var sumy = Correlator.Score(DestinationOnly(7, 3), track, Shahed, 30, Correlator.AnchorOf(DestinationOnly(7, 3), Gazetteer), tAnchor);
        Assert.True(brovary.Total >= 0.6, $"brovary {brovary}");
        Assert.Equal(0, brovary.GapKm);
        Assert.True(sumy.Total < 0.6, $"sumy {sumy}");
        Assert.Equal(0, sumy.Space);
    }

    [Fact]
    public void Report_without_any_place_never_attaches()
    {
        var track = KyivOblastTrack();
        var nowhere = DestinationOnly(999, 1); // unknown destination: no anchor at all
        Assert.Null(Correlator.AnchorOf(nowhere, Gazetteer));
        var score = Correlator.Score(nowhere, track, Shahed, 30, null, Correlator.AnchorOf(track, Gazetteer));
        Assert.Equal(0, score.Space);
        Assert.True(score.Total < 0.6, $"score {score}");
    }

    [Fact]
    public void Destination_only_track_starts_at_the_approach_and_moves_to_the_first_real_fix()
    {
        var track = TrackUpdater.CreateTrack(DestinationOnly(3932, 0), T0, Brovary);
        Assert.Equal(LocationKind.DirectionOnly, track.LastLocationKind);
        Assert.Equal(3932, track.LastLocationPlaceId);
        Assert.Equal(Correlator.DestinationAnchorKm, track.LastLocationAccuracyKm);
        // A real position replaces the anchor and the path does not start at the anchor.
        var fix = Obs(30.6, 50.45, 5, place: 4000);
        fix.LocationAccuracyKm = 3;
        fix.LocationKind = LocationKind.City;
        TrackUpdater.Apply(track, fix, T0.AddMinutes(5), isNewer: true);
        Assert.Equal(LocationKind.City, track.LastLocationKind);
        Assert.Null(track.TrackGeometry);
    }

    [Fact]
    public void Named_destination_moves_the_marker_to_the_approach()
    {
        // "на Сумщині" then "курсом на Бровари": the marker goes to the approach to Brovary, not the oblast centroid;
        // a bearing from an oblast centre would be noise, so no course is derived from a coarse previous position.
        var track = KyivOblastTrack();
        TrackUpdater.Apply(track, DestinationOnly(3932, 4), T0.AddMinutes(4), isNewer: true, Brovary);
        Assert.Equal(3932, track.LastLocationPlaceId);
        Assert.Equal(LocationKind.DirectionOnly, track.LastLocationKind);
        Assert.Equal(Correlator.DestinationAnchorKm, track.LastLocationAccuracyKm);
        Assert.Null(track.DirectionDeg);
    }

    [Fact]
    public void Named_destination_after_a_precise_fix_gives_the_course()
    {
        // Fixed over a town, then "курсом на Бровари": the marker moves to the approach and the arrow points at Brovary.
        var fix = Obs(30.45, 50.30, 0, place: 4000);
        fix.LocationAccuracyKm = 3;
        fix.LocationKind = LocationKind.City;
        var track = TrackUpdater.CreateTrack(fix, T0);
        TrackUpdater.Apply(track, DestinationOnly(3932, 4), T0.AddMinutes(4), isNewer: true, Brovary);
        Assert.Equal(3932, track.LastLocationPlaceId);
        Assert.Equal(DirectionKind.TowardsPlace, track.DirectionKind);
        Assert.Equal(ConfidenceLevel.Low, track.DirectionConfidence);
        Assert.InRange(track.DirectionDeg!.Value, 30, 60);
        Assert.Null(track.TrackGeometry); // the approach anchor is not a fix on the path
    }

    [Fact]
    public void Movement_does_not_override_a_reported_direction()
    {
        var track = TrackUpdater.CreateTrack(Obs(34.8, 50.9, 0, dir: 225), T0);
        TrackUpdater.Apply(track, Obs(34.55, 49.59, 50), T0.AddMinutes(50), isNewer: true);
        Assert.Equal(225, track.DirectionDeg);
        Assert.Equal(DirectionKind.Compass, track.DirectionKind);
    }
}
