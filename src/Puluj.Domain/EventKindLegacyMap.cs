using Puluj.Domain.Enums;

namespace Puluj.Domain;

/// <summary>
/// Plan §8.2 compatibility window: the legacy <see cref="EventType"/> enum ↔ catalog code. Every enum member maps to
/// exactly one code (the seed's <c>metadata.legacy_event_type</c> mirrors this table). Codes without an enum member
/// fall back to <see cref="EventType.Unknown"/> — documented, never an invented legacy value — until the enum column
/// is retired.
/// </summary>
public static class EventKindLegacyMap
{
    public const string Unclassified = "unknown.unclassified";

    private static readonly IReadOnlyDictionary<EventType, string> ToCodeMap = new Dictionary<EventType, string>
    {
        [EventType.Unknown] = Unclassified,
        [EventType.TargetObserved] = "target.observed",
        [EventType.AirRaidAlert] = "alert.air_raid.started",
        [EventType.AlertCancelled] = "alert.air_raid.ended",
        [EventType.TargetCancelled] = "target.cancelled",
        [EventType.ExplosionReport] = "impact.explosion.reported",
        [EventType.AirDefenseActivity] = "air_defence.activity",
    };

    private static readonly IReadOnlyDictionary<string, EventType> ToEnumMap =
        ToCodeMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>Catalog code of a legacy enum value; every member is mapped, so this never fails for a defined value.</summary>
    public static string ToCode(EventType type) =>
        ToCodeMap.TryGetValue(type, out var code) ? code : throw new ArgumentOutOfRangeException(nameof(type), type, "EventType without a catalog mapping");

    /// <summary>Legacy enum for a catalog code, or null when the kind has no enum member (the writer then stores Unknown).</summary>
    public static EventType? ToEventType(string code) => ToEnumMap.TryGetValue(code, out var t) ? t : null;

    /// <summary>What the legacy column receives for a kind: its enum member, or the documented Unknown fallback.</summary>
    public static EventType LegacyOrFallback(string code) => ToEventType(code) ?? EventType.Unknown;

    public static IEnumerable<KeyValuePair<EventType, string>> All => ToCodeMap;
}
