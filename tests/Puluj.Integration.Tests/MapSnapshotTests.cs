using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Puluj.Api.Services;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>
/// The live map only carries what a marker lifetime can still show (see docs/plan-map-ttl-queries.md): tracks last
/// reported inside MapOptions.MaxLifetime, every open alert whatever its age, feed items inside the feed window.
/// Rows are inserted straight into the database and removed afterwards (the other classes count tracks).
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class MapSnapshotTests(PipelineFixture fixture)
{
    private ServiceProvider? Services => fixture.Services;

    [Fact]
    public async Task Live_snapshot_carries_recent_tracks_and_every_open_alert()
    {
        if (Services is null)
        {
            return; // neither PULUJ_TEST_CONNECTION nor Docker available
        }
        var factory = Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var (snapshots, uncached) = await BuildAsync(factory, new MapOptions { SnapshotCacheSeconds = 0 });

        long freshActive, staleActive, freshClosed, staleClosed, openFresh, openOld, ended, freshTarget, staleTarget, freshEvent, staleEvent, unlocatedEvent;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var categoryId = await db.TargetCategories.Select(c => c.TargetCategoryId).OrderBy(x => x).FirstAsync();
            var source = await db.Sources.OrderBy(s => s.SourceId).FirstAsync();
            var placeId = await db.Places.Where(p => p.Level == PlaceLevel.Region).Select(p => p.PlaceId).OrderBy(x => x).FirstAsync();

            TargetTrack Track(TrackStatus status, TimeSpan age) => new()
            {
                Status = status,
                TargetCategoryId = categoryId,
                FirstSeenAt = now - age - TimeSpan.FromMinutes(5),
                LastSeenAt = now - age,
                UpdatedAt = now - age,
                TargetCount = 1,
                DistinctSourceCount = 1,
            };
            AirAlert Alert(TimeSpan age, DateTimeOffset? endedAt, string key) => new()
            {
                PlaceId = placeId,
                AlertType = AirAlertType.AirRaid,
                StartedAt = now - age,
                EndedAt = endedAt,
                SourceId = source.SourceId,
                SourceAlertId = $"map-snapshot-test-{key}-{Guid.NewGuid():N}",
            };
            Target Report(TimeSpan age, EventType type = EventType.TargetObserved, bool located = false) => new()
            {
                SourceId = source.SourceId,
                RawMessageId = 0,
                ObservedAt = now - age,
                EventType = type,
                TargetCategoryId = categoryId,
                LocationKind = located ? LocationKind.Point : LocationKind.Unknown,
                Location = located ? new Point(30, 50) { SRID = 4326 } : null,
            };

            var tracks = new[] { Track(TrackStatus.Active, TimeSpan.FromMinutes(5)), Track(TrackStatus.Active, TimeSpan.FromDays(3)), Track(TrackStatus.Closed, TimeSpan.FromMinutes(30)), Track(TrackStatus.Closed, TimeSpan.FromDays(3)) };
            var alerts = new[] { Alert(TimeSpan.FromMinutes(10), null, "open-fresh"), Alert(TimeSpan.FromDays(3), null, "open-old"), Alert(TimeSpan.FromHours(5), now - TimeSpan.FromHours(4), "ended") };
            db.TargetTracks.AddRange(tracks);
            db.AirAlerts.AddRange(alerts);
            await db.SaveChangesAsync();
            (freshActive, staleActive, freshClosed, staleClosed) = (tracks[0].TargetTrackId, tracks[1].TargetTrackId, tracks[2].TargetTrackId, tracks[3].TargetTrackId);
            (openFresh, openOld, ended) = (alerts[0].AirAlertId, alerts[1].AirAlertId, alerts[2].AirAlertId);

            // Reports need a raw message behind them (FK); one is enough for both.
            var raw = new RawMessage { SourceId = source.SourceId, SourceMessageId = $"map-snapshot-test-{Guid.NewGuid():N}", SourceMessageKey = $"map-snapshot-test-{Guid.NewGuid():N}", SourceRevision = "0", PublishedAt = now, ReceivedAt = now, RawText = "test", ProcessingStatus = ProcessingStatus.Processed, Hash = Guid.NewGuid().ToString("N") };
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            var reports = new[]
            {
                Report(TimeSpan.FromMinutes(20)),
                Report(TimeSpan.FromHours(7)),
                Report(TimeSpan.FromMinutes(10), EventType.ExplosionReport, located: true),
                Report(TimeSpan.FromHours(7), EventType.AirDefenseActivity, located: true),
                Report(TimeSpan.FromMinutes(10), EventType.AirDefenseActivity),
            };
            foreach (var r in reports)
            {
                r.RawMessageId = raw.RawMessageId;
            }
            db.Targets.AddRange(reports);
            await db.SaveChangesAsync();
            (freshTarget, staleTarget, freshEvent, staleEvent, unlocatedEvent) = (reports[0].TargetId, reports[1].TargetId, reports[2].TargetId, reports[3].TargetId, reports[4].TargetId);
        }

        try
        {
            var all = await uncached.LiveAsync(false, CancellationToken.None);
            var ids = all.Tracks.Select(t => t.Id).ToHashSet();
            Assert.Contains(freshActive, ids);
            Assert.Contains(freshClosed, ids);
            Assert.DoesNotContain(staleActive, ids);
            Assert.DoesNotContain(staleClosed, ids);

            var active = await uncached.LiveAsync(true, CancellationToken.None);
            Assert.Contains(freshActive, active.Tracks.Select(t => t.Id));
            Assert.DoesNotContain(freshClosed, active.Tracks.Select(t => t.Id));

            var alertIds = all.Alerts.Select(a => a.Id).ToHashSet();
            Assert.Contains(openFresh, alertIds);
            Assert.Contains(openOld, alertIds); // an open alert is a state: Luhansk has been under one since 2022
            Assert.DoesNotContain(ended, alertIds);

            // Events are short-lived individual reports, shown only when the source named a location.
            Assert.Contains(freshEvent, all.Events.Select(e => e.Id));
            Assert.DoesNotContain(staleEvent, all.Events.Select(e => e.Id));
            Assert.DoesNotContain(unlocatedEvent, all.Events.Select(e => e.Id));

            // The realtime bridge asks with the same windows.
            var notBefore = now - TimeSpan.FromMinutes(120);
            Assert.NotNull(await uncached.TrackAsync(freshActive, CancellationToken.None, notBefore));
            Assert.Null(await uncached.TrackAsync(staleActive, CancellationToken.None, notBefore));
            Assert.NotNull(await uncached.AlertAsync(openOld, CancellationToken.None, notBefore));
            Assert.Null(await uncached.AlertAsync(ended, CancellationToken.None, notBefore));
            Assert.NotNull(await uncached.TargetAsync(freshTarget, CancellationToken.None, now - TimeSpan.FromHours(6)));
            Assert.Null(await uncached.TargetAsync(staleTarget, CancellationToken.None, now - TimeSpan.FromHours(6)));

            // The feed never reaches further back than its window, whatever `since` says.
            var feed = await uncached.RecentTargetsAsync(now - TimeSpan.FromDays(30), null, 5000, CancellationToken.None);
            Assert.Contains(freshTarget, feed.Select(o => o.Id));
            Assert.DoesNotContain(staleTarget, feed.Select(o => o.Id));

            // With the cache on, everyone inside the TTL shares one result.
            var first = await snapshots.LiveAsync(false, CancellationToken.None);
            var second = await snapshots.LiveAsync(false, CancellationToken.None);
            Assert.Same(first, second);
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Targets.Where(t => new[] { freshTarget, staleTarget, freshEvent, staleEvent, unlocatedEvent }.Contains(t.TargetId)).ExecuteDeleteAsync();
            await db.RawMessages.Where(r => r.SourceMessageId.StartsWith("map-snapshot-test-")).ExecuteDeleteAsync();
            await db.TargetTracks.Where(t => new[] { freshActive, staleActive, freshClosed, staleClosed }.Contains(t.TargetTrackId)).ExecuteDeleteAsync();
            await db.AirAlerts.Where(a => a.SourceAlertId.StartsWith("map-snapshot-test-")).ExecuteDeleteAsync();
        }
    }

    /// <summary>Two services over the fixture's database: one with the cache the tests want, one without (for the assertions on fresh rows).</summary>
    private static async Task<(SnapshotService Cached, SnapshotService Uncached)> BuildAsync(IDbContextFactory<PulujDbContext> factory, MapOptions uncachedOptions)
    {
        var refs = new ReferenceCache(factory, NullLogger<ReferenceCache>.Instance);
        await refs.RefreshAsync(CancellationToken.None);
        var mapper = new DtoMapper(refs);
        var cached = Options.Create(new MapOptions());
        var uncached = Options.Create(uncachedOptions);
        return (
            new SnapshotService(factory, mapper, TimeProvider.System, refs, cached),
            new SnapshotService(factory, mapper, TimeProvider.System, refs, uncached));
    }
}
