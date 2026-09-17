using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>U12 locks the distinct raw PublishedAt and fact ObservedAt populations.</summary>
[Collection(PipelineCollection.Name)]
public sealed class StatsSourcesTests(PipelineFixture fixture)
{
    [Fact]
    public async Task U12_Sources_keeps_observed_fact_when_its_raw_revision_is_outside_publication_window()
    {
        await fixture.ResetDataAsync();
        var factory = fixture.Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-1);
        var to = now.AddHours(1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var source = await db.Sources.OrderBy(x => x.SourceId).FirstAsync();
            var category = await db.TargetCategories.OrderBy(x => x.TargetCategoryId).FirstAsync();
            var kind = await db.EventKinds.OrderBy(x => x.EventKindId).FirstAsync();
            var raw = new RawMessage
            {
                SourceId = source.SourceId, SourceMessageId = "u12-outside", SourceMessageKey = "u12-outside", SourceRevision = "0", Hash = "u12-outside",
                PublishedAt = now.AddDays(-2), ReceivedAt = now.AddDays(-2), RawText = "old raw revision", ProcessingStatus = ProcessingStatus.Processed,
            };
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            db.Targets.Add(new Target
            {
                RawMessageId = raw.RawMessageId, SourceId = source.SourceId, SegmentIndex = 0, ObservedAt = now.AddMinutes(-5),
                EventType = EventType.TargetObserved, EventKindId = kind.EventKindId, TargetCategoryId = category.TargetCategoryId,
                LocationKind = LocationKind.Unknown, ModelConfidence = ConfidenceLevel.Unknown, ClassificationConfidence = ConfidenceLevel.Unknown,
                IdentificationMethod = IdentificationMethod.Structured, DirectionKind = DirectionKind.Unknown,
                DirectionConfidence = ConfidenceLevel.Unknown, Confidence = ConfidenceLevel.Medium, ParserVersion = "u12",
            });
            await db.SaveChangesAsync();
        }

        var refs = new ReferenceCache(factory, NullLogger<ReferenceCache>.Instance);
        await refs.RefreshAsync(CancellationToken.None);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var stats = new StatsService(factory, refs, cache, TimeProvider.System);
        var data = await stats.SourcesAsync(from, to, StatsFilter.From(new QueryCollection(), refs), CancellationToken.None);

        Assert.Equal(0, data.Messages);
        Assert.Equal(1, data.Targets);
        Assert.Equal("observedAt", data.FactFilters.TimeBasis);
        Assert.Contains(data.Sources, source => source.Messages == 0 && source.Targets == 1);
    }
}
