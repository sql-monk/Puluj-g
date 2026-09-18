using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;

namespace Puluj.Integration.Tests;

/// <summary>
/// P12 (§8.7, ADR-0008): an admin edit of a kind's presentation is admin-owned from then on — a newer seed refreshes only the
/// policy fields (policy_version, category…) and leaves the edited presentation alone; the audit row records
/// who/why/before/after.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class CatalogAdminTests(PipelineFixture fixture)
{
    private ServiceProvider Services => fixture.Services!;
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();

    [Fact]
    public async Task Admin_owned_presentation_survives_a_newer_seed_while_policy_fields_follow_it()
    {
        var file = await Services.GetRequiredService<SeedFiles>().ReadAsync<EventKindSeeder.EventKindsFile>(EventKindSeeder.FileName, CancellationToken.None);
        Assert.NotNull(file);
        var seeder = Services.GetServices<ISeeder>().OfType<EventKindSeeder>().Single();
        await using var db = await Factory.CreateDbContextAsync();
        // Reset to the file's state (other tests may have bumped the policy version).
        await db.Database.ExecuteSqlRawAsync("UPDATE event_kinds SET presentation_overridden_at = NULL, policy_version = 0");
        await seeder.SeedAsync(db, file, CancellationToken.None);
        db.ChangeTracker.Clear();

        // The admin edit (what PUT /api/admin/event-kinds/{code} does): presentation + the override marker + an audit row, in one transaction.
        var fire = await db.EventKinds.SingleAsync(k => k.Code == "fire.reported");
        var before = Puluj.Admin.Endpoints.CatalogEndpoints.Snapshot(fire);
        fire.MapColor = "#123456";
        fire.MapLifetime = TimeSpan.FromHours(9);
        fire.NameUk = "Пожежа (адмін)";
        fire.MapVisible = false;
        fire.PresentationOverriddenAt = DateTimeOffset.UtcNow;
        db.EventKindAudits.Add(new EventKindAudit { EventKindId = fire.EventKindId, Action = EventKindAudit.Updated, Actor = "ops", Reason = "test", At = DateTimeOffset.UtcNow, Before = before, After = Puluj.Admin.Endpoints.CatalogEndpoints.Snapshot(fire) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A newer seed changes fire's colour and dedup policy: the colour/name/lifetime/visibility stay admin-owned, the policy fields move.
        var newer = file with
        {
            PolicyVersion = file.PolicyVersion + 7,
            Kinds = file.Kinds.Select(k => k.Code == "fire.reported"
                ? k with { MapColor = "#000000", NameUk = "Пожежа (seed)", MapVisible = true }
                : k).ToList(),
        };
        await seeder.SeedAsync(db, newer, CancellationToken.None);
        db.ChangeTracker.Clear();
        var after = await db.EventKinds.AsNoTracking().SingleAsync(k => k.Code == "fire.reported");
        Assert.Equal("#123456", after.MapColor);
        Assert.Equal("Пожежа (адмін)", after.NameUk);
        Assert.Equal(TimeSpan.FromHours(9), after.MapLifetime);
        Assert.False(after.MapVisible);
        Assert.NotNull(after.PresentationOverriddenAt);
        Assert.Equal(newer.PolicyVersion, after.PolicyVersion);
        // A kind the admin never touched follows the file entirely.
        var explosion = await db.EventKinds.AsNoTracking().SingleAsync(k => k.Code == "impact.explosion.reported");
        Assert.Null(explosion.PresentationOverriddenAt);
        Assert.Equal(newer.PolicyVersion, explosion.PolicyVersion);
        // The audit carries before/after of the admin-owned fields only.
        var audit = await db.EventKindAudits.AsNoTracking().Where(a => a.EventKindId == fire.EventKindId).OrderByDescending(a => a.At).FirstAsync();
        Assert.Equal("ops", audit.Actor);
        Assert.NotEqual(audit.Before!.RootElement.GetProperty("mapColor").GetString(), audit.After!.RootElement.GetProperty("mapColor").GetString());
        Assert.Equal("#123456", audit.After.RootElement.GetProperty("mapColor").GetString());

        // Back to the file's state for the other tests (the seeder is a noop on an equal version, so reset explicitly).
        await db.Database.ExecuteSqlRawAsync("UPDATE event_kinds SET presentation_overridden_at = NULL, policy_version = 0; DELETE FROM event_kind_audit");
        await seeder.SeedAsync(db, file, CancellationToken.None);
        await Services.GetRequiredService<Puluj.Processing.Indexes.IndexProvider>().RefreshAsync(CancellationToken.None);
    }

}
