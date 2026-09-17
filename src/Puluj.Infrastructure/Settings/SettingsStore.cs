using Microsoft.EntityFrameworkCore;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Settings;

/// <summary>Read/write access to <see cref="AppSetting"/> rows (admin UI values). Secrets are stored as entered; the API never echoes them.</summary>
public sealed class SettingsStore(IDbContextFactory<PulujDbContext> factory, TimeProvider clock)
{
    /// <summary>Keys whose values must never leave the server.</summary>
    public static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Collectors:AlertsInUa:Token",
        "Collectors:Telegram:ApiHash",
        "Collectors:Telegram:Password",
        "Collectors:Telegram:VerificationCode",
        "Llm:ApiKey",
        "Admin:Token",
    };

    /// <summary>Keys the admin UI may write. Anything else is rejected so the UI cannot rewrite arbitrary configuration.</summary>
    public static readonly HashSet<string> EditableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Collectors:AlertsInUa:Enabled", "Collectors:AlertsInUa:Token", "Collectors:AlertsInUa:BackfillPeriod",
        "Collectors:Telegram:Enabled", "Collectors:Telegram:ApiId", "Collectors:Telegram:ApiHash", "Collectors:Telegram:Phone",
        "Collectors:Telegram:Password", "Collectors:Telegram:VerificationCode", "Collectors:Telegram:AutoJoin", "Collectors:Telegram:BackfillLimit",
        "Collectors:Telegram:BackfillSince", "Collectors:Telegram:HistoryWorkers", "Collectors:Telegram:RpcTimeout",
        "Collectors:Telegram:HistoryRequestInterval", "Collectors:Telegram:HistoryMinimumInterval", "Collectors:Telegram:HistoryMaximumInterval",
        "Llm:Enabled", "Llm:Model", "Llm:ApiKey", "Llm:MaxMessageAgeHours", "Llm:InputUsdPerMillionTokens", "Llm:OutputUsdPerMillionTokens", "Llm:CacheWriteUsdPerMillionTokens", "Llm:CacheReadUsdPerMillionTokens",
        "Correlation:AttachThreshold", "Correlation:CandidateWindowMinutes", "Correlation:AmbiguityMargin", "Correlation:SlackKm", "Correlation:CoarseLocationAccuracyKm",
        "Admin:Token",
    };

    public async Task<Dictionary<string, AppSetting>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    /// <summary>Upserts values; null or empty removes the key so the appsettings/env value applies again.</summary>
    public async Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var (key, value) in values)
        {
            var existing = await db.AppSettings.FindAsync([key], ct);
            if (string.IsNullOrEmpty(value))
            {
                if (existing is not null)
                {
                    db.AppSettings.Remove(existing);
                }
                continue;
            }
            if (existing is null)
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value, IsSecret = SecretKeys.Contains(key), UpdatedAt = now });
            }
            else
            {
                existing.Value = value;
                existing.IsSecret = SecretKeys.Contains(key);
                existing.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runtime status written by the Worker (e.g. Telegram login state) for the admin UI. Not a user setting.</summary>
    public Task SetStatusAsync(string key, string? value, CancellationToken ct) =>
        SetAsync(new Dictionary<string, string?> { [$"Runtime:{key}"] = value ?? "" }, ct);
}
