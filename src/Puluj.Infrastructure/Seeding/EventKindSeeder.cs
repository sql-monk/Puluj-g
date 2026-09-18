using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Seeding;

/// <summary>
/// Plan §8.2: seeds event_kinds from data/taxonomy/event-kinds.json. The database owns the rows — the file only adds
/// codes that are missing, and refreshes presentation/policy fields of existing rows when the file's policyVersion is
/// newer than the row's. Codes are never renamed or deleted here; evidence (targets.event_kind_id) is never touched.
/// </summary>
public sealed class EventKindSeeder(SeedFiles files, IOptions<SeedOptions> options, ILogger<EventKindSeeder> logger) : ISeeder
{
    public const string FileName = "taxonomy/event-kinds.json";

    /// <summary>After the taxonomy (10) and sources (20): the backfill (90) needs the rows in place.</summary>
    public int Order => 30;

    public async Task SeedAsync(PulujDbContext db, CancellationToken ct)
    {
        if (!options.Value.SeedEventKinds)
        {
            return;
        }
        var file = await files.ReadAsync<EventKindsFile>(FileName, ct);
        if (file is null)
        {
            logger.LogWarning("{File} not found under {Root}; skipping", FileName, files.Root);
            return;
        }
        await SeedAsync(db, file, ct);
    }

    /// <summary>Seeds from an in-memory file (tests feed a modified policy version to prove the admin-owned presentation survives a refresh).</summary>
    public async Task SeedAsync(PulujDbContext db, EventKindsFile file, CancellationToken ct)
    {
        Validate(file);

        var existing = await db.EventKinds.ToDictionaryAsync(k => k.Code, StringComparer.Ordinal, ct);
        var added = 0;
        var refreshed = 0;
        foreach (var k in file.Kinds)
        {
            if (!existing.TryGetValue(k.Code, out var row))
            {
                row = new EventKind { Code = k.Code, NameUk = k.NameUk };
                db.EventKinds.Add(row);
                existing[k.Code] = row;
                Apply(row, k, file.PolicyVersion);
                added++;
            }
            else if (file.PolicyVersion > row.PolicyVersion)
            {
                if (row.PresentationOverriddenAt is not null)
                {
                    // P12 (ADR-0008): an admin owns the presentation of this row — only the seed-owned policy fields follow the newer file.
                    ApplyPolicy(row, k, file.PolicyVersion);
                }
                else
                {
                    // Presentation/policy follow the newer seed; the code (identity) and any admin-owned Enabled flag stay.
                    var enabled = row.Enabled;
                    Apply(row, k, file.PolicyVersion);
                    row.Enabled = enabled;
                }
                refreshed++;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Event kinds: {Count} in file (policy v{Version}), {Added} new, {Refreshed} refreshed", file.Kinds.Count, file.PolicyVersion, added, refreshed);
    }

    /// <summary>Icons the map can draw (P12 catalog editor validates against the same list, so seed and admin never drift); the client maps them to glyph shapes.</summary>
    public static readonly string[] IconVocabulary = ["air-defence", "alert", "alert-off", "cancel", "damage", "evacuation", "explosion", "fire", "impact", "interception", "launch", "notice", "outage", "target", "unknown"];

    /// <summary>Seed invariants that tests also check: contract code pattern, known category, unique codes, legacy map parity, icon vocabulary.</summary>
    public static void Validate(EventKindsFile file)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in file.Kinds)
        {
            if (!EventKindCodes.IsValid(k.Code))
            {
                throw new InvalidOperationException($"event-kinds.json: code '{k.Code}' does not match the contract pattern");
            }
            if (!codes.Add(k.Code))
            {
                throw new InvalidOperationException($"event-kinds.json: duplicate code '{k.Code}'");
            }
            if (!Enum.GetNames<EventKindCategory>().Any(n => string.Equals(n, k.Category, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"event-kinds.json: unknown category '{k.Category}' for '{k.Code}'");
            }
            if (k.MapIcon is { } icon && !IconVocabulary.Contains(icon, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"event-kinds.json: unknown mapIcon '{icon}' for '{k.Code}'");
            }
            EventType? legacy;
            try
            {
                legacy = k.LegacyEventType;
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"event-kinds.json: '{k.Code}' has an unknown metadata.legacyEventType", ex);
            }
            if (legacy is not null && EventKindLegacyMap.ToCode(legacy.Value) != k.Code)
            {
                throw new InvalidOperationException($"event-kinds.json: '{k.Code}' claims legacy {legacy} but the map says {EventKindLegacyMap.ToCode(legacy.Value)}");
            }
        }
        foreach (var (type, code) in EventKindLegacyMap.All)
        {
            if (!codes.Contains(code))
            {
                throw new InvalidOperationException($"event-kinds.json: legacy {type} maps to '{code}' which the seed does not define");
            }
        }
    }

    /// <summary>Everything from the file: the admin-owned presentation fields and the seed-owned policy fields.</summary>
    private static void Apply(EventKind row, EventKindSeedEntry k, int policyVersion)
    {
        row.NameUk = k.NameUk;
        row.RequiresLocationForMap = k.RequiresLocationForMap ?? false;
        row.RenderMode = k.RenderMode;
        row.MapColor = k.MapColor;
        row.MapIcon = k.MapIcon;
        row.MapLifetime = k.MapLifetime is null ? null : TimeSpan.Parse(k.MapLifetime);
        row.Enabled = k.Enabled ?? true;
        row.MapVisible = k.MapVisible ?? true;
        row.SortOrder = k.SortOrder ?? 0;
        ApplyPolicy(row, k, policyVersion);
    }

    /// <summary>The seed-owned fields only: category, severity, state model, metadata, presentation json and policy version.</summary>
    private static void ApplyPolicy(EventKind row, EventKindSeedEntry k, int policyVersion)
    {
        row.Category = Enum.Parse<EventKindCategory>(k.Category, true);
        row.DefaultSeverity = k.DefaultSeverity;
        row.StateModel = k.StateModel;
        row.Presentation = ToDoc(k.Presentation);
        row.Metadata = ToDoc(k.Metadata);
        row.PolicyVersion = policyVersion;
    }

    private static JsonDocument? ToDoc(JsonElement? e) => e is null || e.Value.ValueKind == JsonValueKind.Undefined ? null : JsonDocument.Parse(e.Value.GetRawText());

    public sealed record EventKindsFile(int PolicyVersion, List<EventKindSeedEntry> Kinds);

    public sealed record EventKindSeedEntry(
        string Code, string NameUk, string Category, string? DefaultSeverity, string? StateModel, bool? RequiresLocationForMap,
        string? RenderMode, string? MapColor, string? MapIcon, string? MapLifetime, bool? Enabled, bool? MapVisible,
        int? SortOrder, JsonElement? Presentation, JsonElement? Metadata)
    {
        /// <summary><c>metadata.legacyEventType</c>, when the kind mirrors a legacy enum member.</summary>
        public EventType? LegacyEventType =>
            Metadata is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty("legacyEventType", out var v) && v.ValueKind == JsonValueKind.String
                ? Enum.Parse<EventType>(v.GetString()!, true)
                : null;
    }
}
