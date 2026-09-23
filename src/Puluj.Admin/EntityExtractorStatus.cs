using System.Globalization;
using Puluj.Contracts;
using Puluj.Infrastructure.EntityExtraction;
using Puluj.Infrastructure.Settings;

namespace Puluj.Admin;

/// <summary>What the delivery queue of the Entity Extractor holds right now. `failed` is terminal: a failed delivery is not retried.</summary>
/// <param name="LastSuccessAt">Latest successful completion, including deliveries that finish out of enqueue order.</param>
public sealed record EeQueueSnapshot(
    long Queued, long InProgress, long Failed, long FailedLastHour, long FailedLast24h,
    DateTimeOffset? LastFailureAt, string? LastError, DateTimeOffset? OldestQueuedAt, DateTimeOffset? LastSuccessAt);

/// <summary>Answer of the extractor's own health endpoint.</summary>
public sealed record EeProbe(bool Available, int? StatusCode, string? Error);

/// <summary>
/// One line of state for the Entity Extractor, the same on the overview and on its own page. A health 200 alone is not
/// "working": failures of the last hour make it a warning, while the terminal failures of the past are counted and dated
/// but do not turn a currently healthy extractor red.
/// </summary>
public static class EntityExtractorStatus
{
    public static ServiceStatusDto Describe(EeProbe probe, EeQueueSnapshot queue, bool llmEnabled, DateTimeOffset now)
    {
        var status = !probe.Available ? "down"
            : queue.FailedLastHour > 0 ? "warn"
            : queue.Queued > 0 && queue.OldestQueuedAt is { } oldest && now - oldest > TimeSpan.FromHours(1) && (queue.LastSuccessAt is null || now - queue.LastSuccessAt > TimeSpan.FromMinutes(15)) ? "warn"
            : "ok";
        var parts = new List<string>();
        parts.Add(probe.Available ? $"health {probe.StatusCode}" : $"недоступний: {probe.Error ?? $"HTTP {probe.StatusCode}"}");
        parts.Add($"черга {Num(queue.Queued)}{(queue.InProgress > 0 ? $", у роботі {Num(queue.InProgress)}" : "")}");
        parts.Add(queue.Failed == 0
            ? "невдалих доставок немає"
            : $"невдалих: {Num(queue.FailedLastHour)} за годину, {Num(queue.FailedLast24h)} за 24 год, {Num(queue.Failed)} усього (остання {Ago(queue.LastFailureAt, now)})");
        parts.Add(llmEnabled ? "LLM увімкнено" : "LLM вимкнено навмисно");
        return new ServiceStatusDto("entity-extractor", status, string.Join(" · ", parts), queue.LastSuccessAt);
    }

    private static string Num(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("uk-UA"));

    private static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "—";
        var span = now - at.Value;
        if (span < TimeSpan.FromMinutes(1)) return "щойно";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} хв тому";
        if (span < TimeSpan.FromDays(2)) return $"{(int)span.TotalHours} год тому";
        return $"{(int)span.TotalDays} дн тому";
    }
}

/// <summary>One EntityExtractor:* setting as the admin page edits it: what is stored, where the applied value comes from, and what it means.</summary>
/// <param name="Value">Value stored in app_settings; null when nothing is stored there.</param>
/// <param name="Source">`db` (app_settings), `config` (appsettings / environment) or `default` (built into the service).</param>
/// <param name="Effective">The value the delivery loop applies.</param>
public sealed record EeSettingDto(string Key, string Label, string? Value, string Source, string Effective, string Default, string Format, string Hint);

/// <summary>Descriptions and validation of the EntityExtractor:* settings; defaults come from <see cref="EntityDeliveryOptions"/>.</summary>
public static class EntityExtractorSettings
{
    private sealed record Descriptor(string Key, string Label, string Default, string Format, string Hint, Func<string, string?> Validate);

    private static readonly EntityDeliveryOptions Defaults = new();

    private static readonly Descriptor[] All =
    [
        new("EntityExtractor:Url", "Адреса Entity Extractor", Defaults.Url, "http(s)://хост:порт", "Куди processor надсилає повідомлення на обробку.", ValidateUrl),
        new("EntityExtractor:DeliveryTimeout", "Таймаут доставки", Span(Defaults.DeliveryTimeout), "гг:хх:сс", "Скільки чекати відповіді на одне повідомлення, від 1 с до 10 хв.", v => ValidateSpan(v, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10))),
        new("EntityExtractor:PollingInterval", "Пауза при порожній черзі", Span(Defaults.PollingInterval), "гг:хх:сс", "Як часто перевіряти чергу, коли вона порожня, від 0,1 с до 5 хв.", v => ValidateSpan(v, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(5))),
        new("EntityExtractor:ClaimLease", "Оренда доставки", Span(Defaults.ClaimLease), "гг:хх:сс", "Через скільки завислу доставку забере інший воркер. Понад 15 хв відновлення завислих запусків у EE, не більше 24 год.", ValidateClaimLease),
        new("EntityExtractor:Concurrency", "Паралельних доставок", Defaults.Concurrency.ToString(CultureInfo.InvariantCulture), "ціле число", $"Скільки повідомлень надсилати одночасно, від 1 до {EntityDeliveryOptions.MaxConcurrency}.", ValidateConcurrency),
    ];

    public static IReadOnlyList<string> Keys { get; } = All.Select(d => d.Key).ToArray();

    /// <param name="stored">Values in app_settings (only the keys that are stored).</param>
    /// <param name="configured">Resolves a key from appsettings / environment.</param>
    public static IReadOnlyList<EeSettingDto> Describe(IReadOnlyDictionary<string, string?> stored, Func<string, string?> configured) =>
        All.Select(d =>
        {
            var db = stored.TryGetValue(d.Key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
            var config = configured(d.Key);
            var (source, effective) = db is not null ? ("db", db) : !string.IsNullOrWhiteSpace(config) ? ("config", config!) : ("default", d.Default);
            // Existing DB/config values may predate validation; describe the worker's actual clamp while retaining the stored value.
            if (d.Key == "EntityExtractor:Concurrency" && int.TryParse(effective, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency))
                effective = Math.Clamp(concurrency, 1, EntityDeliveryOptions.MaxConcurrency).ToString(CultureInfo.InvariantCulture);
            return new EeSettingDto(d.Key, d.Label, db, source, effective, d.Default, d.Format, d.Hint);
        }).ToList();

    /// <summary>Resolve appsettings/environment precedence without a possibly stale app_settings provider.</summary>
    public static string? ConfigurationFallback(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
            throw new ArgumentException("A configuration root is required to inspect fallback providers.", nameof(configuration));
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider is DbConfigurationProvider) continue;
            if (provider.TryGet(key, out var value)) return value;
        }
        return null;
    }

    /// <summary>The first problem of the submitted values, or null. Empty values are allowed: they remove the stored value.</summary>
    public static string? Validate(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            var descriptor = All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null) return $"Тут можна змінювати лише налаштування EntityExtractor:*; «{key}» не підтримується.";
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (descriptor.Validate(value.Trim()) is { } problem) return $"{descriptor.Label}: {problem}";
        }
        return null;
    }

    private static string Span(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    private static string? ValidateUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? null : "потрібна абсолютна адреса http:// або https://.";

    private static string? ValidateSpan(string value, TimeSpan min, TimeSpan max) =>
        !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) ? "очікується тривалість у форматі гг:хх:сс, наприклад 00:00:30."
        : span < min || span > max ? $"має бути від {Span(min)} до {Span(max)}."
        : null;

    private static string? ValidateConcurrency(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var workers) && workers is >= 1 and <= EntityDeliveryOptions.MaxConcurrency ? null : $"ціле число від 1 до {EntityDeliveryOptions.MaxConcurrency}.";

    private static string? ValidateClaimLease(string value) =>
        !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) ? "очікується тривалість у форматі гг:хх:сс."
        : span <= TimeSpan.FromMinutes(15) || span > TimeSpan.FromHours(24) ? "має бути понад 00:15:00 і не більше 1.00:00:00."
        : null;
}
