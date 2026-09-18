using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>U04's composition query runs against PostGIS: no fake provider can validate the correlated EXISTS predicates.</summary>
[Collection(PipelineCollection.Name)]
public sealed class PublicCatalogQueriesTests(PipelineFixture fixture)
{
    [Fact]
    public async Task U04_Canonical_track_suppresses_list_observation_but_direct_detail_and_alert_remain_available()
    {
        var factory = fixture.Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE track_targets, target_track_revisions, target_tracks, target_links, targets, air_alerts, raw_messages RESTART IDENTITY CASCADE");
        var source = await db.Sources.OrderBy(x => x.SourceId).FirstAsync();
        var category = await db.TargetCategories.OrderBy(x => x.TargetCategoryId).FirstAsync();
        var kind = await db.EventKinds.OrderBy(x => x.EventKindId).FirstAsync();
        var place = await db.Places.Where(x => x.Level == PlaceLevel.Region).OrderBy(x => x.PlaceId).FirstAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        var raw = new RawMessage
        {
            SourceId = source.SourceId, SourceMessageId = "u04-1", SourceMessageKey = "u04-1", SourceRevision = "0", Hash = "u04-hash",
            PublishedAt = at, ReceivedAt = at, RawText = "must never be returned by public catalogue",
        };
        db.RawMessages.Add(raw);
        await db.SaveChangesAsync();
        var target = new Target
        {
            TargetId = 9_007_199_254_740_993, RawMessageId = raw.RawMessageId, SourceId = source.SourceId, SegmentIndex = 0, ObservedAt = at,
            EventType = EventType.Unknown, EventKindId = kind.EventKindId, TargetCategoryId = category.TargetCategoryId,
            LocationKind = LocationKind.Region, LocationPlaceId = place.PlaceId, Location = place.Centroid, LocationAccuracyKm = place.RadiusKm,
            ModelConfidence = ConfidenceLevel.Unknown, ClassificationConfidence = ConfidenceLevel.Low, IdentificationMethod = IdentificationMethod.Structured,
            DirectionKind = DirectionKind.Unknown, DirectionConfidence = ConfidenceLevel.Unknown, Confidence = ConfidenceLevel.Medium, ParserVersion = "u04",
        };
        var track = new TargetTrack
        {
            TargetCategoryId = category.TargetCategoryId, Status = TrackStatus.Active, FirstSeenAt = at, LastSeenAt = at, UpdatedAt = at,
            LastLocationKind = LocationKind.Region, LastLocationPlaceId = place.PlaceId, LastLocation = place.Centroid, LastLocationAccuracyKm = place.RadiusKm,
            DirectionKind = DirectionKind.Unknown, DirectionConfidence = ConfidenceLevel.Unknown, ModelConfidence = ConfidenceLevel.Unknown, TrackConfidence = ConfidenceLevel.Medium,
            TargetCount = 1, DistinctSourceCount = 1,
        };
        db.AddRange(target, track);
        await db.SaveChangesAsync();
        db.TrackTargets.Add(new TrackTarget { TargetTrackId = track.TargetTrackId, TargetId = target.TargetId, Sequence = 1, AssociationConfidence = 0.9 });
        db.AirAlerts.Add(new AirAlert { PlaceId = place.PlaceId, SourceId = source.SourceId, SourceAlertId = "u04-alert", AlertType = AirAlertType.AirRaid, Level = AirAlertLevel.Unknown, StartedAt = at });
        await db.SaveChangesAsync();
        await db.DisposeAsync();

        var refs = new ReferenceCache(factory, NullLogger<ReferenceCache>.Instance);
        await refs.RefreshAsync(CancellationToken.None);
        var catalogue = new PublicCatalogQueries(factory, refs, TimeProvider.System);
        var page = await catalogue.ListAsync(new PublicCatalogQueries.Query(
            EntityKinds: "track,alert,observation", Q: null, EventKinds: null, EventCategories: null, CategoryIds: null, ClassIds: null, FamilyIds: null, ModelIds: null,
            SourceIds: null, RegionId: null, Status: null, Confidence: null, Location: null, HasResults: null, Sort: null,
            From: at.AddHours(-1), To: at.AddHours(1), Cursor: null, PageSize: 50, Dataset: "live", HistoryBasis: null, At: null), CancellationToken.None);

        Assert.Contains(page.Items, x => x.Kind == "track");
        Assert.Contains(page.Items, x => x.Kind == "alert");
        Assert.DoesNotContain(page.Items, x => x.Kind == "observation" && x.Id == target.TargetId.ToString());
        Assert.Equal(target.TargetId.ToString(), (await catalogue.DetailsAsync("observation", target.TargetId, "live", null, null, CancellationToken.None))!.Entity.Id);
        var messages = await catalogue.MessagePageAsync("track", track.TargetTrackId, null, 50, "live", CancellationToken.None);
        Assert.DoesNotContain("text", System.Text.Json.JsonSerializer.Serialize(messages));
    }
}
