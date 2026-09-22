using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Seeding;

/// <summary>
/// Upserts sources from data/sources.json by Code. Never touches CollectorState. A Telegram channel that the
/// database already covers under another code is skipped, whatever the file calls it: two rows for one channel
/// make the collector read it twice (docs/naming.md, "Коди джерел").
/// </summary>
public sealed class SourceSeeder(SeedFiles files, IOptions<SeedOptions> options, IConfiguration config, ILogger<SourceSeeder> logger) : ISeeder
{
    public const string FileName = "sources.json";

    public int Order => 20;

    public async Task SeedAsync(PulujDbContext db, CancellationToken ct)
    {
        if (!options.Value.SeedSources)
        {
            return;
        }
        var data = await files.ReadAsync<SourcesFile>(FileName, ct);
        if (data is null)
        {
            logger.LogWarning("sources.json not found under {Root}; skipping", files.Root);
            return;
        }
        await SeedAsync(db, data, ct);
        await AdoptConfiguredTokenAsync(db, ct);
    }

    /// <summary>Adds the file's new codes; returns what was added and what was skipped as a duplicate channel.</summary>
    public async Task<SeedResult> SeedAsync(PulujDbContext db, SourcesFile data, CancellationToken ct)
    {
        // The database owns the sources: the file only introduces codes that are not there yet, so whatever the
        // admin UI changed (name, trust, channel, polling, token) survives every restart.
        var existing = await db.Sources.AsNoTracking().ToListAsync(ct);
        var codes = existing.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        var added = new List<string>();
        var skipped = new List<SkippedSource>();
        foreach (var s in data.Sources.Where(s => !codes.Contains(s.Code)))
        {
            var type = Enum.Parse<SourceType>(s.Type, true);
            var source = new Source
            {
                Code = s.Code,
                Name = s.Name,
                Enabled = s.Enabled ?? true,
                Type = type,
                Url = s.Url,
                TrustLevel = s.TrustLevel ?? 0.5,
                Priority = s.Priority ?? 0,
                PollingInterval = s.PollingInterval is null ? null : TimeSpan.Parse(s.PollingInterval),
                Config = s.Config is null ? null : JsonDocument.Parse(s.Config.Value.GetRawText()),
            };
            // A channel already present under another code (a legacy code, or one created through the admin API
            // with the tg_ convention) is the same source; a second row would collect it twice.
            if (type == SourceType.Telegram && SourceCodes.FindTelegramChannel(existing, SourceCodes.TelegramChannel(source)) is { } dup)
            {
                skipped.Add(new SkippedSource(s.Code, dup.Code, SourceCodes.TelegramChannel(source)!));
                logger.LogWarning("sources.json: '{Code}' skipped — Telegram channel @{Channel} is already source '{Existing}'", s.Code, SourceCodes.TelegramChannel(source), dup.Code);
                continue;
            }
            db.Sources.Add(source);
            existing.Add(source); // so a second file entry for the same channel is caught too
            codes.Add(s.Code);
            added.Add(s.Code);
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Sources: {Count} in file, {Added} new, {Skipped} skipped as duplicate channels", data.Sources.Count, added.Count, skipped.Count);
        return new SeedResult(added, skipped);
    }

    /// <summary>
    /// One-time move of a token given through configuration / env into the source row, so the settings page shows
    /// and owns it. Configuration stays a fallback for fresh deployments that ship the token in .env.
    /// </summary>
    private async Task AdoptConfiguredTokenAsync(PulujDbContext db, CancellationToken ct)
    {
        var token = config["Collectors:AlertsInUa:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }
        var alerts = await db.Sources.FirstOrDefaultAsync(s => s.Code == "alerts_in_ua", ct);
        if (alerts is null || !string.IsNullOrEmpty(alerts.Secret("token")))
        {
            return;
        }
        alerts.Secrets = JsonDocument.Parse(JsonSerializer.Serialize(new { token = token.Trim() }));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("alerts.in.ua token adopted from configuration into the source row");
    }

    public sealed record SourcesFile(List<SourceDto> Sources);
    public sealed record SourceDto(string Code, string Name, string Type, string? Url, double? TrustLevel, int? Priority, bool? Enabled, string? PollingInterval, JsonElement? Config);
    public sealed record SeedResult(IReadOnlyList<string> Added, IReadOnlyList<SkippedSource> Skipped);
    /// <summary>A file entry not added because <see cref="ExistingCode"/> already collects the same Telegram channel.</summary>
    public sealed record SkippedSource(string Code, string ExistingCode, string Channel);
}
