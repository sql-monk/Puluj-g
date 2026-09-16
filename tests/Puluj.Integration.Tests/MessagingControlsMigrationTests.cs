using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>P13 O07: `AddMessagingControls` is additive and reversible — Down drops the two control tables and the two delivery columns, Up restores them with their CHECK and indexes.</summary>
[Collection(PipelineCollection.Name)]
public sealed class MessagingControlsMigrationTests(PipelineFixture fixture)
{
    private IDbContextFactory<PulujDbContext> Factory => fixture.Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();

    [Fact]
    public async Task Migration_down_removes_controls_and_up_restores_them()
    {
        await using var db = await Factory.CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var index = applied.FindIndex(m => m.EndsWith("_AddMessagingControls", StringComparison.Ordinal));
        Assert.True(index > 0, "AddMessagingControls must be applied");
        await migrator.MigrateAsync(applied[index - 1]);
        Assert.False(await TableExists(db, "messaging.subscription_lanes"));
        Assert.False(await TableExists(db, "messaging.control_audit"));
        Assert.False(await ColumnExists(db, "deliveries", "lane"));
        Assert.False(await ColumnExists(db, "deliveries", "occurred_at"));

        await migrator.MigrateAsync();
        Assert.True(await TableExists(db, "messaging.subscription_lanes"));
        Assert.True(await TableExists(db, "messaging.control_audit"));
        Assert.True(await ColumnExists(db, "deliveries", "lane"));
        Assert.True(await ColumnExists(db, "deliveries", "occurred_at"));
        Assert.True(await IndexExists(db, "ix_processing_deliveries_pending_lane"));
        Assert.True(await IndexExists(db, "ix_processing_deliveries_completed_brin"));
        // The CHECK constraint refuses an unknown state.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO messaging.subscription_lanes (subscription_id, lane, state, changed_at) VALUES ('parser', 'live', 'stopped', now())"));
        await db.Database.ExecuteSqlRawAsync("INSERT INTO messaging.subscription_lanes (subscription_id, lane, state, changed_at) VALUES ('parser', 'live', 'paused', now())");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM messaging.subscription_lanes WHERE subscription_id = 'parser'");
    }

    private static Task<bool> TableExists(PulujDbContext db, string table) =>
        db.Database.SqlQueryRaw<bool>("SELECT to_regclass({0}) IS NOT NULL AS \"Value\"", table).SingleAsync();

    private static Task<bool> ColumnExists(PulujDbContext db, string table, string column) =>
        db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = {0} AND column_name = {1}) AS \"Value\"", table, column).SingleAsync();

    private static Task<bool> IndexExists(PulujDbContext db, string name) =>
        db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = {0}) AS \"Value\"", name).SingleAsync();
}
