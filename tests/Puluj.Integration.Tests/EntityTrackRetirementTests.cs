using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.EntityApi;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

[Collection(PipelineCollection.Name)]
public sealed class EntityTrackRetirementTests(PipelineFixture fixture)
{
    [Fact]
    public async Task Upgrade_preserves_track_rows_but_removes_them_from_public_reads()
    {
        await fixture.ResetDataAsync();
        var services = fixture.Services!;
        var factory = services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT enabled AS \"Value\" FROM ee_entity_definitions WHERE entity_name='track'").SingleAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260924060000_EntityPlacesAndEventTime");
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE ee_extractors SET enabled=true WHERE name='track';
                UPDATE ee_entity_definitions SET enabled=true,
                    map_settings=map_settings || '{"enabled":true}'::jsonb
                WHERE entity_name='track';
                """);
            var raw = new RawMessage
            {
                SourceId = await db.Sources.Select(s => s.SourceId).FirstAsync(),
                SourceMessageId = "ee-retirement-history",
                SourceMessageKey = "ee-retirement-history",
                SourceRevision = "0",
                RawText = "Historical route",
                Hash = new string('a', 64),
                ReceivedAt = DateTimeOffset.UtcNow,
                PublishedAt = DateTimeOffset.UtcNow,
            };
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ee_tracks(raw_message_id, occurred_at, label, geometry, attributes)
                VALUES ({raw.RawMessageId}, now(), 'Historical route',
                    ST_GeomFromText('LINESTRING(30 50,31 51)',4326), jsonb_build_object('retained',true));
                INSERT INTO ee_targets(raw_message_id, occurred_at, label)
                VALUES ({raw.RawMessageId}, now(), 'Retained target');
                """);
            var before = await db.Database.SqlQueryRaw<string>(
                "SELECT row_to_json(t)::text AS \"Value\" FROM ee_tracks t").SingleAsync();
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                "SELECT enabled AS \"Value\" FROM ee_extractors WHERE name='track'").SingleAsync());
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                "SELECT enabled AND (map_settings->>'enabled')::boolean AS \"Value\" FROM ee_entity_definitions WHERE entity_name='track'").SingleAsync());
            var api = new EntityQueries(services.GetRequiredService<IConfiguration>());
            // New API hides retired data even when the registry has not been migrated yet.
            Assert.Empty((await api.CatalogueAsync("track", null, null, null, null, null, 20, 0, default)).Items);

            await migrator.MigrateAsync();

            var after = await db.Database.SqlQueryRaw<string>(
                "SELECT row_to_json(t)::text AS \"Value\" FROM ee_tracks t").SingleAsync();
            Assert.Equal(before, after);
            Assert.False(await db.Database.SqlQueryRaw<bool>(
                "SELECT enabled AS \"Value\" FROM ee_extractors WHERE name='track'").SingleAsync());
            Assert.False(await db.Database.SqlQueryRaw<bool>(
                "SELECT enabled OR (map_settings->>'enabled')::boolean AS \"Value\" FROM ee_entity_definitions WHERE entity_name='track'").SingleAsync());
            Assert.DoesNotContain(await api.DefinitionsAsync(default), d => d.EntityName == "track");
            Assert.DoesNotContain((await api.SnapshotAsync(null, default)).Items, x => x.Entity == "track");
            Assert.DoesNotContain((await api.SnapshotAsync(DateTimeOffset.UtcNow, default)).Items, x => x.Entity == "track");
            Assert.Empty((await api.CatalogueAsync("track", null, null, null, null, null, 20, 0, default)).Items);
            Assert.Empty((await api.CatalogueAsync("ee_tracks", null, null, null, null, null, 20, 0, default)).Items);
            Assert.Null(await api.DetailAsync("track", "1", default));
            Assert.Empty(await api.HistoryAsync("track", "1", 20, default));
            var targets = await api.CatalogueAsync("target", null, null, null, null, null, 20, 0, default);
            Assert.True(targets.ExcludesRetiredTracks);
            Assert.Single(targets.Items);
            Assert.DoesNotContain(await api.HistoryAsync("target", targets.Items[0].Id, 20, default), x => x.Entity == "track");
        }
        finally
        {
            // Restore the shared disposable fixture even when an assertion fails.
            await migrator.MigrateAsync();
        }
    }
}
