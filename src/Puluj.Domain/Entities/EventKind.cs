using System.Text.Json;
using Puluj.Domain.Enums;

namespace Puluj.Domain.Entities;

/// <summary>
/// Plan §8.2. One row per kind of fact the system recognises (target.observed, alert.air_raid.started, impact.explosion.reported...).
/// The database owns the rows: the seed file only introduces codes that are not there yet and refreshes presentation
/// when its policy version is newer. <see cref="Code"/> is the stable identity used by contracts (observation.event_kind_code)
/// and never changes; presentation changes never rewrite evidence.
/// </summary>
public class EventKind
{
    public int EventKindId { get; set; }
    /// <summary>Stable code, e.g. <c>impact.explosion.reported</c>; matches the contract pattern <c>^[a-z_]+(\.[a-z_]+)+$</c>.</summary>
    public required string Code { get; set; }
    public required string NameUk { get; set; }
    /// <summary>Which domain owner handles observations of this kind (contract observationCategory).</summary>
    public EventKindCategory Category { get; set; }
    /// <summary>Free vocabulary from the seed (info, low, medium, high); absent = not assessed.</summary>
    public string? DefaultSeverity { get; set; }
    /// <summary>How the state of a fact of this kind evolves: none, track, interval, reported_confirmed_resolved.</summary>
    public string? StateModel { get; set; }
    /// <summary>Limits display, not storage: a fact without a usable location is still stored.</summary>
    public bool RequiresLocationForMap { get; set; }
    /// <summary>marker, area, interval, feed.</summary>
    public string? RenderMode { get; set; }
    public string? MapColor { get; set; }
    public string? MapIcon { get; set; }
    /// <summary>Presentation only: how long the map shows the fact. Not a retention period.</summary>
    public TimeSpan? MapLifetime { get; set; }
    public bool Enabled { get; set; } = true;
    public bool MapVisible { get; set; } = true;
    public int SortOrder { get; set; }
    public JsonDocument? Presentation { get; set; }
    /// <summary>Seed metadata, e.g. <c>legacyEventType</c> — the <see cref="EventType"/> member this kind maps to.</summary>
    public JsonDocument? Metadata { get; set; }
    /// <summary>
    /// P12 (ADR-0008): set when an admin edited the presentation fields (name, map colour/icon/lifetime/visibility, render mode,
    /// sort order, enabled, requires-location). A newer seed then refreshes only the seed-owned fields (category, severity, state
    /// model, metadata, presentation json, policy_version) and leaves these alone.
    /// </summary>
    public DateTimeOffset? PresentationOverriddenAt { get; set; }
    /// <summary>Version of the seed policy the row last took its presentation from; the seeder only overwrites when the file is newer.</summary>
    public int PolicyVersion { get; set; }
}
