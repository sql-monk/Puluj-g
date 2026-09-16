using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;

namespace Puluj.Integration.Tests;

/// <summary>
/// P12 (§8.7, ADR-0008): an admin edit of a kind's presentation is admin-owned from then on — a newer seed refreshes only the
/// policy fields (dedup_policy, policy_version, category…) and leaves the edited presentation alone; the audit row records
/// who/why/before/after; the review queue's jsonb predicates walk the bounded window through the read index.
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
                ? k with { MapColor = "#000000", NameUk = "Пожежа (seed)", MapVisible = true, DedupPolicy = JsonSerializer.SerializeToElement(new { windowMinutes = 999, slackKm = 1 }) }
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
        Assert.Equal(999, after.DedupPolicy!.RootElement.GetProperty("windowMinutes").GetInt32());
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

    [Fact]
    public async Task Review_queue_finds_ambiguous_and_near_links_inside_the_window()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE incident_revisions, incident_observations, incidents RESTART IDENTITY CASCADE");
        var kind = await db.EventKinds.AsNoTracking().Where(k => k.Code == "impact.explosion.reported").Select(k => k.EventKindId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 3; i++)
        {
            db.Incidents.Add(new Incident { GenerationId = Guid.Empty, EventKindId = kind, State = Incident.Reported, FirstReportedAt = now.AddMinutes(-i), LastReportedAt = now.AddMinutes(-i), EventAt = now.AddMinutes(-i), Confidence = Domain.Enums.ConfidenceLevel.Medium, SourceCount = 1, Revision = 1, CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync();
        db.IncidentObservations.AddRange(
            new IncidentObservation { IncidentId = 1, ObservationId = Guid.NewGuid(), GenerationId = Guid.Empty, SourceId = 1, Relation = IncidentObservation.Canonical, Score = 1, EffectiveAt = now, LinkedAt = now, PolicyVersion = "t", DecisionReason = JsonDocument.Parse("""{"considered": 0}""") },
            new IncidentObservation { IncidentId = 2, ObservationId = Guid.NewGuid(), GenerationId = Guid.Empty, SourceId = 1, Relation = IncidentObservation.Ambiguous, Score = 0.7, EffectiveAt = now, LinkedAt = now, PolicyVersion = "t", DecisionReason = JsonDocument.Parse("""{"considered": 2, "ambiguous": [1, 3]}""") },
            new IncidentObservation { IncidentId = 3, ObservationId = Guid.NewGuid(), GenerationId = Guid.Empty, SourceId = 2, Relation = IncidentObservation.Canonical, Score = 1, EffectiveAt = now, LinkedAt = now, PolicyVersion = "t", DecisionReason = JsonDocument.Parse("""{"considered": 1, "near_candidates": [1]}""") });
        await db.SaveChangesAsync();

        // The same predicate the endpoint uses: only the flagged links, newest first, inside the window.
        var since = now.AddHours(-24);
        var flagged = await db.IncidentObservations.AsNoTracking()
            .Where(Puluj.Admin.Endpoints.IncidentEndpoints.ReviewPredicate(since))
            .Select(o => o.IncidentId)
            .ToListAsync();
        Assert.Equal([2, 3], flagged.Order());
        // The merge plan on the ambiguous pair is what the operator gets offered.
        var pair = await db.Incidents.AsNoTracking().Include(i => i.Observations).Where(i => i.IncidentId == 2 || i.IncidentId == 1).ToListAsync();
        var plan = Puluj.Processing.Incidents.IncidentStateWriter.PlanMerge(pair.Single(i => i.IncidentId == 2), pair.Single(i => i.IncidentId == 1));
        Assert.True(plan.Allowed);
        Assert.Single(plan.MovedObservationIds);
        Assert.Equal(1, plan.SourceCountAfter); // the same channel on both sides
        await db.Database.ExecuteSqlRawAsync("TRUNCATE incident_revisions, incident_observations, incidents RESTART IDENTITY CASCADE");
    }
}
