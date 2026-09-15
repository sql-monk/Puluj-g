using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Admin;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Pipeline;

namespace Puluj.Integration.Tests;

/// <summary>End-to-end over a real PostGIS database (see <see cref="PipelineFixture"/>): seed -> ingest raw messages -> parse -> correlate -> replay.</summary>
[Collection(PipelineCollection.Name)]
public sealed class PipelineTests(PipelineFixture fixture)
{
    private ServiceProvider? _services => fixture.Services;

    [Fact]
    public async Task Pipeline_report_builds_all_supported_periods()
    {
        if (_services is null)
        {
            return; // neither PULUJ_TEST_CONNECTION nor Docker available
        }

        var factory = _services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var now = DateTimeOffset.UtcNow;
        foreach (var hours in PipelineBuckets.AllowedHours)
        {
            await using var db = await factory.CreateDbContextAsync();
            var report = await PipelineReport.BuildAsync(db, hours, now, CancellationToken.None);

            Assert.Equal(PipelineBuckets.Unit(hours), report.Bucket);
            Assert.Equal(hours <= 48 ? hours : hours / 24, report.Timeline.Count);
        }
    }

    [Fact]
    public async Task Raw_messages_become_targets_tracks_and_revisions()
    {
        if (_services is null)
        {
            return; // neither PULUJ_TEST_CONNECTION nor Docker available
        }
        var factory = _services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var ingestor = _services.GetRequiredService<RawMessageIngestor>();
        var processor = _services.GetRequiredService<RawMessageProcessor>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-40);

        int sourceId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
        }

        var first = await ingestor.IngestAsync(Msg(sourceId, "m1", t0, "Шахеди на Сумщині курсом на Полтавщину."), "tg_kpszsu", CancellationToken.None);
        var second = await ingestor.IngestAsync(Msg(sourceId, "m2", t0.AddMinutes(30), "БпЛА на Полтавщині у південному напрямку."), "tg_kpszsu", CancellationToken.None);
        var duplicate = await ingestor.IngestAsync(Msg(sourceId, "m1", t0, "Шахеди на Сумщині курсом на Полтавщину."), "tg_kpszsu", CancellationToken.None);
        Assert.True(first.IsNew && second.IsNew);
        Assert.False(duplicate.IsNew); // idempotent on (source, source_message_id)

        Assert.Equal(1, await processor.ProcessAsync(first.RawMessageId!.Value, CancellationToken.None));
        Assert.Equal(1, await processor.ProcessAsync(second.RawMessageId!.Value, CancellationToken.None));
        Assert.Equal(0, await processor.ProcessAsync(second.RawMessageId!.Value, CancellationToken.None)); // already processed

        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.TargetTracks.SingleAsync();
            Assert.Equal(TrackStatus.Active, track.Status);
            Assert.Equal(2, track.TargetCount);
            Assert.Equal("Полтавська область", await db.Places.Where(p => p.PlaceId == track.LastLocationPlaceId).Select(p => p.Name).SingleAsync());
            Assert.Null(track.TrackGeometry); // two adjacent oblasts overlap: a line between their centres is not a route
            Assert.Equal(2, await db.TargetTrackRevisions.CountAsync(r => r.TargetTrackId == track.TargetTrackId));
            Assert.Equal(2, await db.TrackTargets.CountAsync());
            var processed = (await db.RawMessages.FindAsync(first.RawMessageId))!;
            Assert.Equal(ProcessingStatus.Processed, processed.ProcessingStatus);
            Assert.NotNull(processed.ProcessingMs); // the wall time of the successful run is kept for the pipeline report

            // Replay: before the second message only the first revision exists.
            var replayAt = t0.AddMinutes(10);
            var revision = await db.TargetTrackRevisions.Where(r => r.RevisionAt <= replayAt).OrderByDescending(r => r.RevisionAt).FirstAsync();
            Assert.Equal(1, revision.TargetCount);
        }

        // A levelled alert stated in text (Kyiv oblast administration style) becomes an AirAlert interval with its level,
        // and the oblast-wide "відбій" closes it.
        var yellow = await ingestor.IngestAsync(Msg(sourceId, "m3", t0.AddMinutes(35), "🟡 Броварський район — повітряна тривога, жовтий рівень: Дронова загроза (жовтий рівень)"), "tg_kpszsu", CancellationToken.None);
        Assert.True(await processor.ProcessAsync(yellow.RawMessageId!.Value, CancellationToken.None) >= 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var alert = await db.AirAlerts.SingleAsync(a => a.SourceAlertId.StartsWith("text:"));
            Assert.Equal(AirAlertLevel.Yellow, alert.Level);
            Assert.Null(alert.EndedAt);
            Assert.Equal(1, await db.TargetTracks.CountAsync()); // the named cause is not a sighting
        }
        // A "відбій" from before the alert started (out-of-order delivery, a replayed old message) leaves it open.
        var stale = await ingestor.IngestAsync(Msg(sourceId, "m3b", t0.AddMinutes(30), "Київська область: відбій тривоги."), "tg_kpszsu", CancellationToken.None);
        Assert.Equal(1, await processor.ProcessAsync(stale.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Null((await db.AirAlerts.SingleAsync(a => a.SourceAlertId.StartsWith("text:"))).EndedAt);
        }
        var cancel = await ingestor.IngestAsync(Msg(sourceId, "m4", t0.AddMinutes(50), "Київська область — відбій повітряної тривоги."), "tg_kpszsu", CancellationToken.None);
        Assert.Equal(1, await processor.ProcessAsync(cancel.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.NotNull((await db.AirAlerts.SingleAsync(a => a.SourceAlertId.StartsWith("text:"))).EndedAt);
        }
    }

    [Fact]
    public async Task Alert_end_handled_before_its_start_still_closes_the_interval()
    {
        if (_services is null)
        {
            return;
        }
        var factory = _services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var ingestor = _services.GetRequiredService<RawMessageIngestor>();
        var processor = _services.GetRequiredService<RawMessageProcessor>();
        int sourceId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "alerts_in_ua").Select(s => s.SourceId).SingleAsync();
        }
        var startedAt = Ms(DateTimeOffset.UtcNow.AddHours(-2)); // timestamptz keeps microseconds, not .NET ticks
        var endedAt = startedAt.AddMinutes(40);
        var alertJson = "{\"id\":777,\"alert_type\":\"air_raid\",\"location_type\":\"oblast\",\"location_title\":\"Київська область\","
                        + "\"location_oblast\":\"Київська область\",\"started_at\":\"" + startedAt.ToString("O") + "\"}";
        var start = await ingestor.IngestAsync(Alert(sourceId, "777:start", startedAt, "alert.started", alertJson), "alerts_in_ua", CancellationToken.None);
        var end = await ingestor.IngestAsync(Alert(sourceId, "777:end", endedAt, "alert.finished", alertJson), "alerts_in_ua", CancellationToken.None);

        // A rebuild replays the start hours after the live end was handled: the end must not be lost.
        Assert.Equal(1, await processor.ProcessAsync(end.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var a = await db.AirAlerts.SingleAsync(x => x.SourceAlertId == "777");
            Assert.Equal(startedAt, a.StartedAt);
            Assert.Equal(endedAt, a.EndedAt);
            Assert.Null(a.StartRawMessageId);
        }
        Assert.Equal(1, await processor.ProcessAsync(start.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var a = await db.AirAlerts.SingleAsync(x => x.SourceAlertId == "777");
            Assert.Equal(endedAt, a.EndedAt); // still closed
            Assert.Equal(start.RawMessageId, a.StartRawMessageId);
        }
    }

    [Fact]
    public async Task History_alert_start_with_finished_at_is_stored_closed()
    {
        if (_services is null)
        {
            return;
        }
        var factory = _services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var ingestor = _services.GetRequiredService<RawMessageIngestor>();
        var processor = _services.GetRequiredService<RawMessageProcessor>();
        int sourceId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Where(s => s.Code == "alerts_in_ua").Select(s => s.SourceId).SingleAsync();
        }
        var startedAt = Ms(DateTimeOffset.UtcNow.AddDays(-20));
        var endedAt = startedAt.AddMinutes(25);
        // The history endpoint returns the whole alert, so the start message already knows when it ended.
        var alertJson = "{\"id\":778,\"alert_type\":\"air_raid\",\"location_type\":\"oblast\",\"location_title\":\"Кіровоградська область\","
                        + "\"location_oblast\":\"Кіровоградська область\",\"started_at\":\"" + startedAt.ToString("O") + "\",\"finished_at\":\"" + endedAt.ToString("O") + "\"}";
        var start = await ingestor.IngestAsync(Alert(sourceId, "778:start", startedAt, "alert.started", alertJson), "alerts_in_ua", CancellationToken.None);
        var end = await ingestor.IngestAsync(Alert(sourceId, "778:end", endedAt, "alert.finished", alertJson), "alerts_in_ua", CancellationToken.None);

        Assert.Equal(1, await processor.ProcessAsync(start.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var a = await db.AirAlerts.SingleAsync(x => x.SourceAlertId == "778");
            Assert.Equal(endedAt, a.EndedAt); // never open, so the map is not flashed with a month-old alert
        }
        Assert.Equal(1, await processor.ProcessAsync(end.RawMessageId!.Value, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var a = await db.AirAlerts.SingleAsync(x => x.SourceAlertId == "778");
            Assert.Equal(endedAt, a.EndedAt);
            Assert.Equal(end.RawMessageId, a.EndRawMessageId);
        }
    }

    private static DateTimeOffset Ms(DateTimeOffset t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMillisecond));

    private static IncomingMessage Alert(int sourceId, string id, DateTimeOffset at, string kind, string alertJson) => new()
    {
        SourceId = sourceId,
        SourceMessageId = id,
        PublishedAt = at,
        RawPayload = JsonDocument.Parse($"{{\"kind\":\"{kind}\",\"at\":\"{at:O}\",\"alert\":{alertJson}}}"),
    };

    private static IncomingMessage Msg(int sourceId, string id, DateTimeOffset at, string text) => new()
    {
        SourceId = sourceId,
        SourceMessageId = id,
        PublishedAt = at,
        RawText = text,
        RawPayload = JsonDocument.Parse("{\"kind\":\"test\"}"),
    };
}
