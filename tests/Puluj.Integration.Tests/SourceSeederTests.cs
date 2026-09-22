using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;

namespace Puluj.Integration.Tests;

/// <summary>
/// The seed file only introduces codes the database does not have — and never a second row for a Telegram channel the
/// database already collects under another code (a legacy code, or one created through the admin API as tg_…), because
/// the collector would then read that channel twice. docs/naming.md, "Коди джерел".
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class SourceSeederTests(PipelineFixture fixture)
{
    private IDbContextFactory<PulujDbContext> Factory => fixture.Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private SourceSeeder Seeder => fixture.Services!.GetServices<ISeeder>().OfType<SourceSeeder>().Single();

    [Fact]
    public async Task A_channel_already_in_the_database_is_not_seeded_again_under_another_code()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sources WHERE code LIKE 'seedtest_%' OR code LIKE 'tg_seedtest_%'");
        db.Sources.Add(Telegram("tg_seedtest_alpha", "SeedTest_Alpha"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var file = new SourceSeeder.SourcesFile(
        [
            Entry("seedtest_alpha", "seedtest_alpha"),          // same channel, legacy code, different case: skipped
            Entry("tg_seedtest_alpha", "SeedTest_Alpha"),       // code already present: not touched (existing behaviour)
            Entry("seedtest_beta", "@SeedTest_Beta"),           // new channel: added
            Entry("seedtest_beta_again", "https://t.me/seedtest_beta"), // duplicate within the file: skipped
        ]);
        var result = await Seeder.SeedAsync(db, file, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(["seedtest_beta"], result.Added);
        Assert.Equal(
            [("seedtest_alpha", "tg_seedtest_alpha"), ("seedtest_beta_again", "seedtest_beta")],
            result.Skipped.Select(s => (s.Code, s.ExistingCode)).ToArray());

        var codes = await db.Sources.AsNoTracking().Where(s => s.Code.Contains("seedtest_")).Select(s => s.Code).OrderBy(c => c).ToListAsync();
        Assert.Equal(["seedtest_beta", "tg_seedtest_alpha"], codes);

        // Second run is a no-op: everything is either present or a known duplicate.
        var again = await Seeder.SeedAsync(db, file, CancellationToken.None);
        Assert.Empty(again.Added);
        Assert.Equal(2, again.Skipped.Count);

        await db.Database.ExecuteSqlRawAsync("DELETE FROM sources WHERE code LIKE 'seedtest_%' OR code LIKE 'tg_seedtest_%'");
    }

    private static Source Telegram(string code, string channel) => new()
    {
        Code = code,
        Name = code,
        Type = SourceType.Telegram,
        Url = $"https://t.me/{channel}",
        Config = JsonDocument.Parse(JsonSerializer.Serialize(new { channel, language = "uk", official = false })),
    };

    private static SourceSeeder.SourceDto Entry(string code, string channel) =>
        new(code, code, "Telegram", null, 0.6, 60, true, null, JsonSerializer.SerializeToElement(new { channel, language = "uk", official = false }));
}
