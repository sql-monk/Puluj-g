using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// Plan §8.7 catalog editor (P12, ADR-0008): the presentation of an event kind — name, map colour/icon/lifetime/visibility,
/// render mode, sort order, enabled, requires-location — is admin-owned once edited (`presentation_overridden_at`); the
/// policy fields (category, severity, state model, metadata, policy_version) stay with the
/// seed. Every change writes an audit row (actor, reason, before/after). Disabling a kind that carries a legacy mapping
/// would silence its P08 rules — refused without `force`.
/// </summary>
public static partial class CatalogEndpoints
{
    /// <summary>Icons the map can draw (the client maps them to glyph shapes; anything else falls back to a circle) — the server is the vocabulary.</summary>
    public static readonly string[] IconVocabulary = Puluj.Infrastructure.Seeding.EventKindSeeder.IconVocabulary;
    public static readonly string[] RenderModes = ["marker", "area", "feed"];
    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    public sealed record UpdateRequest(string? NameUk, bool? MapVisible, string? MapColor, string? MapIcon, string? MapLifetime, string? RenderMode, int? SortOrder, bool? Enabled, bool? RequiresLocationForMap,
        string? Actor, string? Reason, bool? Force);

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/event-kinds").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        g.MapGet("", async (IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.EventKinds.AsNoTracking().OrderBy(k => k.SortOrder).ThenBy(k => k.Code).ToListAsync(ct);
            return Results.Ok(new { iconVocabulary = IconVocabulary, renderModes = RenderModes, kinds = rows.Select(Dto) });
        });

        g.MapGet("/{code}/audit", async (string code, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var kind = await db.EventKinds.AsNoTracking().FirstOrDefaultAsync(k => k.Code == code, ct);
            if (kind is null)
            {
                return Results.NotFound();
            }
            var rows = await db.EventKindAudits.AsNoTracking().Where(a => a.EventKindId == kind.EventKindId).OrderByDescending(a => a.At).Take(200).ToListAsync(ct);
            return Results.Ok(rows.Select(a => new { a.AuditId, a.Action, a.Actor, a.Reason, a.At, Before = a.Before?.RootElement, After = a.After?.RootElement }));
        });

        g.MapPut("/{code}", async (string code, UpdateRequest req, IDbContextFactory<PulujDbContext> factory, AdminIndexes indexes, TimeProvider clock, CancellationToken ct) =>
        {
            if (Validate(req) is { } invalid)
            {
                return invalid;
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var kind = await db.EventKinds.FirstOrDefaultAsync(k => k.Code == code, ct);
            if (kind is null)
            {
                return Results.NotFound();
            }
            if (req.Enabled == false && kind.Enabled && HasLegacyMapping(kind) && req.Force != true)
            {
                return Results.Conflict(new { error = $"{code} maps a legacy EventType: disabling it silences its rules (P08) — a pipeline change, not presentation; pass force=true to confirm" });
            }
            if (!HasPresentationChange(req) && req.Enabled is null)
            {
                return Results.BadRequest(new { error = "nothing to change" });
            }
            var before = Snapshot(kind);
            Apply(kind, req);
            if (HasPresentationChange(req))
            {
                kind.PresentationOverriddenAt = clock.GetUtcNow(); // an enabled-only toggle keeps the seed's ownership of the presentation (the seeder preserves Enabled on its own)
            }
            var after = Snapshot(kind);
            var action = req.Enabled is { } enabled && enabled != before.RootElement.GetProperty("enabled").GetBoolean() ? (enabled ? EventKindAudit.Enabled : EventKindAudit.Disabled) : EventKindAudit.Updated;
            db.EventKindAudits.Add(new EventKindAudit { EventKindId = kind.EventKindId, Action = action, Actor = req.Actor!, Reason = req.Reason!, At = clock.GetUtcNow(), Before = before, After = after });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            indexes.Invalidate(); // the admin's own parser/preview indexes; the API and the workers refresh on their 10-minute poll
            return Results.Ok(Dto(kind));
        });
        return app;
    }

    /// <summary>400 without actor/reason; 422 for a malformed colour, an icon outside the vocabulary, a lifetime outside 1 min..7 d, an unknown render mode; null when the request is fine.</summary>
    public static IResult? Validate(UpdateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Actor) || string.IsNullOrWhiteSpace(req.Reason))
        {
            return Results.BadRequest(new { error = "actor and reason are required" });
        }
        var problems = new List<string>();
        if (req.MapColor is { } color && !ColorPattern().IsMatch(color))
        {
            problems.Add("mapColor must be #rrggbb");
        }
        if (req.MapIcon is { } icon && !IconVocabulary.Contains(icon, StringComparer.Ordinal))
        {
            problems.Add($"mapIcon must be one of {string.Join(", ", IconVocabulary)}");
        }
        if (req.MapLifetime is { } lifetime && (!TimeSpan.TryParseExact(lifetime, [@"h\:mm\:ss", @"hh\:mm\:ss", @"d\.hh\:mm\:ss", @"d\.h\:mm\:ss"], System.Globalization.CultureInfo.InvariantCulture, out var span) || span < MinLifetime || span > MaxLifetime))
        {
            problems.Add("mapLifetime must be hh:mm:ss or d.hh:mm:ss between 00:01:00 and 7.00:00:00");
        }
        if (req.RenderMode is { } mode && !RenderModes.Contains(mode, StringComparer.Ordinal))
        {
            problems.Add($"renderMode must be one of {string.Join(", ", RenderModes)}");
        }
        if (req.NameUk is { } name && (name.Trim().Length == 0 || name.Length > 160))
        {
            problems.Add("nameUk must be 1..160 characters");
        }
        if (req.SortOrder is < 0 or > 10_000)
        {
            problems.Add("sortOrder must be 0..10000");
        }
        return problems.Count == 0 ? null : Results.UnprocessableEntity(new { error = "invalid presentation", problems });
    }

    /// <summary>A field other than `enabled` is in the patch: from then on the presentation is admin-owned (ADR-0008).</summary>
    public static bool HasPresentationChange(UpdateRequest req) =>
        req.NameUk is not null || req.MapVisible is not null || req.MapColor is not null || req.MapIcon is not null || req.MapLifetime is not null || req.RenderMode is not null || req.SortOrder is not null || req.RequiresLocationForMap is not null;

    public static bool HasLegacyMapping(EventKind kind) =>
        kind.Metadata is { } m && m.RootElement.ValueKind == JsonValueKind.Object && m.RootElement.TryGetProperty("legacyEventType", out var t) && t.ValueKind == JsonValueKind.String;

    private static void Apply(EventKind kind, UpdateRequest req)
    {
        if (req.NameUk is { } name) kind.NameUk = name.Trim();
        if (req.MapVisible is { } visible) kind.MapVisible = visible;
        if (req.MapColor is { } color) kind.MapColor = color.ToLowerInvariant();
        if (req.MapIcon is { } icon) kind.MapIcon = icon;
        if (req.MapLifetime is { } lifetime) kind.MapLifetime = TimeSpan.Parse(lifetime, System.Globalization.CultureInfo.InvariantCulture);
        if (req.RenderMode is { } mode) kind.RenderMode = mode;
        if (req.SortOrder is { } order) kind.SortOrder = order;
        if (req.Enabled is { } enabled) kind.Enabled = enabled;
        if (req.RequiresLocationForMap is { } requires) kind.RequiresLocationForMap = requires;
    }

    /// <summary>The admin-owned fields as one JSON object (the audit's before/after).</summary>
    public static JsonDocument Snapshot(EventKind k) => JsonDocument.Parse(new JsonObject
    {
        ["nameUk"] = k.NameUk,
        ["mapVisible"] = k.MapVisible,
        ["mapColor"] = k.MapColor,
        ["mapIcon"] = k.MapIcon,
        ["mapLifetime"] = k.MapLifetime?.ToString(),
        ["renderMode"] = k.RenderMode,
        ["sortOrder"] = k.SortOrder,
        ["enabled"] = k.Enabled,
        ["requiresLocationForMap"] = k.RequiresLocationForMap,
    }.ToJsonString());

    private static object Dto(EventKind k) => new
    {
        k.EventKindId, k.Code, k.NameUk, Category = k.Category.ToString().ToLowerInvariant(), k.DefaultSeverity, k.StateModel, k.RequiresLocationForMap, k.RenderMode, k.MapColor, k.MapIcon,
        MapLifetime = k.MapLifetime?.ToString(), k.Enabled, k.MapVisible, k.SortOrder, k.PolicyVersion, k.PresentationOverriddenAt,
        LegacyEventType = HasLegacyMapping(k) ? k.Metadata!.RootElement.GetProperty("legacyEventType").GetString() : null,
        Metadata = k.Metadata?.RootElement,
    };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
