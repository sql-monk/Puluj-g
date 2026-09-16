using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>U05 exercises the public raw revision boundary on a disposable PostGIS database.</summary>
[Collection(PipelineCollection.Name)]
public sealed class PublicMessageQueriesTests(PipelineFixture fixture)
{
    [Fact]
    public async Task U05_List_keeps_raw_without_facts_and_detail_keeps_all_message_facts()
    {
        var factory = fixture.Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE track_targets, target_tracks, target_links, targets, air_alerts, raw_messages RESTART IDENTITY CASCADE");
        var source = await db.Sources.OrderBy(x => x.SourceId).FirstAsync();
        var category = await db.TargetCategories.OrderBy(x => x.TargetCategoryId).FirstAsync();
        var kind = await db.EventKinds.OrderBy(x => x.EventKindId).FirstAsync();
        var place = await db.Places.Where(x => x.Level == PlaceLevel.Region).OrderBy(x => x.PlaceId).FirstAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        var noFacts = new RawMessage { SourceId = source.SourceId, SourceMessageId = "u05-empty", SourceMessageKey = "u05-empty", SourceRevision = "0", Hash = "u05-empty", PublishedAt = at, ReceivedAt = at, RawText = null, ProcessingStatus = ProcessingStatus.Pending };
        var multi = new RawMessage { SourceId = source.SourceId, SourceMessageId = "u05-post", SourceMessageKey = "u05-post", SourceRevision = "e42", Hash = "u05-post", PublishedAt = at.AddSeconds(1), ReceivedAt = at, RawText = "public text", ProcessingStatus = ProcessingStatus.Processed, Url = "javascript:never" };
        db.AddRange(noFacts, multi);
        await db.SaveChangesAsync();
        db.Targets.AddRange(
            Fact(multi.RawMessageId, source.SourceId, kind.EventKindId, category.TargetCategoryId, place.PlaceId, at, 9_007_199_254_740_993, 0),
            Fact(multi.RawMessageId, source.SourceId, kind.EventKindId, category.TargetCategoryId, place.PlaceId, at.AddSeconds(1), 9_007_199_254_740_994, 1));
        await db.SaveChangesAsync();
        await db.DisposeAsync();

        var refs = new ReferenceCache(factory, NullLogger<ReferenceCache>.Instance);
        await refs.RefreshAsync(CancellationToken.None);
        var messages = new PublicMessageQueries(factory, refs, TimeProvider.System);
        var page = await messages.ListAsync(new PublicMessageQueries.Query(null, null, null, null, null, null, null, null, null, null, null, null, at.AddHours(-1), at.AddHours(1), null, 50, "live"), CancellationToken.None);
        Assert.Contains(page.Items, x => x.Id == noFacts.RawMessageId.ToString() && x.ResultCount == 0 && x.Outcome == "pending");
        var detail = (await messages.DetailsAsync(multi.RawMessageId, "live", CancellationToken.None))!;
        Assert.Equal(2, detail.Results.TotalCount);
        Assert.All(detail.Results.Items, item => Assert.NotNull(item.SegmentText));
        Assert.Null(detail.Message.Url);
        Assert.Equal(multi.RawMessageId.ToString(), detail.Message.Id);
    }

    private static Target Fact(long rawId, int sourceId, int eventKindId, int categoryId, int placeId, DateTimeOffset at, long id, int segment) => new()
    {
        TargetId = id, RawMessageId = rawId, SourceId = sourceId, SegmentIndex = segment, ObservedAt = at, EventType = EventType.Unknown,
        EventKindId = eventKindId, TargetCategoryId = categoryId, LocationKind = LocationKind.Region, LocationPlaceId = placeId,
        ModelConfidence = ConfidenceLevel.Unknown, ClassificationConfidence = ConfidenceLevel.Low, IdentificationMethod = IdentificationMethod.Structured,
        DirectionKind = DirectionKind.Unknown, DirectionConfidence = ConfidenceLevel.Unknown, Confidence = ConfidenceLevel.Medium,
        ParserVersion = "u05", SegmentText = $"segment {segment}",
    };
}
