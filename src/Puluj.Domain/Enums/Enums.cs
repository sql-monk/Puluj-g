namespace Puluj.Domain.Enums;

public enum SourceType
{
    RestApi = 1,
    Telegram = 2,
    Rss = 3,
    Web = 4,
}

public enum ProcessingStatus
{
    Pending = 0,
    Processed = 1,
    Failed = 2,
    Skipped = 3,
    /// <summary>Claimed by a processor instance (claimed_by / claimed_at); goes back to Pending when the claim lease expires.</summary>
    InProgress = 4,
}

/// <summary>How precise the location of an target is. Never upgrade a vague text to a Point.</summary>
public enum LocationKind
{
    Unknown = 0,
    DirectionOnly = 1,
    Region = 2,
    District = 3,
    City = 4,
    Area = 5,
    Point = 6,
}

/// <summary>Alert levels used by regional administrations (Kyiv oblast): yellow = target (usually drones), red = missiles / imminent.
/// Structured feeds such as alerts.in.ua carry no level and stay Unknown.</summary>
public enum AirAlertLevel
{
    Unknown = 0,
    Yellow = 1,
    Red = 2,
}

public enum DirectionKind
{
    Unknown = 0,
    Compass = 1,
    TowardsPlace = 2,
}

/// <summary>Spec §9 confidence levels. Ordered so that higher value == more confident.</summary>
public enum ConfidenceLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Confirmed = 4,
}

public enum IdentificationMethod
{
    /// <summary>Structured source (e.g. alerts.in.ua API) — no text parsing involved.</summary>
    Structured = 0,
    Rule = 1,
    Llm = 2,
    Manual = 3,
}

/// <summary>Spec §11 event types. Target subtypes live in TargetClass/TargetModel, not here.</summary>
public enum EventType
{
    Unknown = 0,
    /// <summary>A target object is reported (moving, present, launched).</summary>
    TargetObserved = 1,
    AirRaidAlert = 10,
    AlertCancelled = 11,
    TargetCancelled = 12,
    ExplosionReport = 20,
    AirDefenseActivity = 21,
}

/// <summary>
/// Which presentation family an observation kind belongs to; stored as a lowercase string.
/// </summary>
public enum EventKindCategory
{
    Target = 0,
    Alert = 1,
    Event = 2,
    /// <summary>Feed-only: no track or alert owner (civil notices, unknown).</summary>
    Info = 3,
}

public enum TrackStatus
{
    Active = 0,
    Closed = 1,
    Cancelled = 2,
}

public enum PlaceLevel
{
    Country = 0,
    Region = 1,
    District = 2,
    Hromada = 3,
    City = 4,
    Town = 5,
    Village = 6,
    /// <summary>Non-administrative named area (sea, gulf, airfield, etc.).</summary>
    NamedArea = 7,
}

/// <summary>Which level of the target hierarchy an alias resolves to.</summary>
public enum AliasTargetLevel
{
    Category = 0,
    Class = 1,
    Family = 2,
    Model = 3,
}

public enum AirAlertType
{
    Unknown = 0,
    AirRaid = 1,
    ArtilleryShelling = 2,
    UrbanFights = 3,
    Chemical = 4,
    Nuclear = 5,
}
