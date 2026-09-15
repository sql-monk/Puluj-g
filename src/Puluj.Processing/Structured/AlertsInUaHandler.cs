using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Text;

namespace Puluj.Processing.Structured;

/// <summary>
/// Handles RawMessages produced by the alerts.in.ua collector (payload kind alert.started / alert.finished):
/// maintains AirAlert intervals and emits AirRaidAlert / AlertCancelled targets without any NLP.
/// The alert is pinned to the place of the level the feed names — oblast, raion, hromada or city — so the map
/// colours exactly the polygon that is under alert (raions and hromadas come from COD-AB, see GazetteerSeeder).
/// Start and end may arrive in either order (a rebuild replays the start hours after the live end was handled):
/// an end without a start stores the closed interval from the payload, a start that finds it only fills in its message.
/// </summary>
public sealed class AlertsInUaHandler(IIndexes indexes, INormalizer normalizer, ILogger<AlertsInUaHandler> logger)
{
    public const string Version = "alerts_in_ua-0.2";

    public static bool CanHandle(RawMessage raw) =>
        raw.RawPayload is not null
        && raw.RawPayload.RootElement.TryGetProperty("kind", out var k)
        && k.GetString() is "alert.started" or "alert.finished";

    /// <summary>What the feed says about one alert, as the collector wrapped it (`{kind, at, alert{…}}`); no place resolution yet.</summary>
    public sealed record AlertPayload(string Kind, string SourceAlertId, string Title, string Oblast, string? Raion, string LocationType, string? AlertTypeText, AirAlertType AlertType, string? LevelText, AirAlertLevel Level, DateTimeOffset At, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
    {
        public bool IsStart => Kind == "alert.started";
    }

    public static AlertPayload ParsePayload(JsonElement root, DateTimeOffset fallbackAt)
    {
        var kind = root.GetProperty("kind").GetString()!;
        var alert = root.GetProperty("alert");
        var title = Str(alert, "location_title") ?? "";
        var alertTypeText = Str(alert, "alert_type");
        var alertType = alertTypeText switch
        {
            "air_raid" => AirAlertType.AirRaid,
            "artillery_shelling" => AirAlertType.ArtilleryShelling,
            "urban_fights" => AirAlertType.UrbanFights,
            "chemical" => AirAlertType.Chemical,
            "nuclear" => AirAlertType.Nuclear,
            _ => AirAlertType.Unknown,
        };
        // The feed carries the administration's level where one is published (yellow = drones, red = missiles).
        var levelText = Str(alert, "alert_level");
        var level = levelText?.ToLowerInvariant() switch
        {
            "yellow" => AirAlertLevel.Yellow,
            "red" => AirAlertLevel.Red,
            _ => AirAlertLevel.Unknown,
        };
        var at = root.TryGetProperty("at", out var atEl) && DateTimeOffset.TryParse(atEl.GetString(), out var parsed) ? parsed : fallbackAt;
        var startedAt = DateTimeOffset.TryParse(Str(alert, "started_at"), out var s) ? s : at;
        var finishedAt = DateTimeOffset.TryParse(Str(alert, "finished_at"), out var f) ? f : (DateTimeOffset?)null;
        return new AlertPayload(kind, alert.GetProperty("id").ToString(), title, Str(alert, "location_oblast") ?? title, Str(alert, "location_raion"),
            Str(alert, "location_type") ?? "oblast", alertTypeText, alertType, levelText, level, at, startedAt, finishedAt);
    }

    public async Task<List<Target>> HandleAsync(PulujDbContext db, RawMessage raw, Source source, CancellationToken ct)
    {
        var payload = ParsePayload(raw.RawPayload!.RootElement, raw.PublishedAt);
        var (kind, sourceAlertId, title, oblast, raion, locationType) = (payload.Kind, payload.SourceAlertId, payload.Title, payload.Oblast, payload.Raion, payload.LocationType);
        var alertType = payload.AlertType;
        var level = payload.Level;
        var at = payload.At;
        var alert = raw.RawPayload.RootElement.GetProperty("alert");

        var place = ResolvePlace(locationType, title, raion, oblast);
        if (place is null)
        {
            logger.LogWarning("alerts.in.ua: unknown location '{Title}' ({Type}) / '{Oblast}'", title, locationType, oblast);
        }
        else if (LevelOf(locationType) is { } wanted && place.Level != wanted)
        {
            logger.LogInformation("alerts.in.ua: '{Title}' ({Type}) resolved to {Level} '{Place}'", title, locationType, place.Level, place.Name);
        }

        var existing = await db.AirAlerts.FirstOrDefaultAsync(a => a.SourceId == source.SourceId && a.SourceAlertId == sourceAlertId, ct);
        var startedAt = payload.StartedAt;
        // A history load carries the whole alert in the start message too: store it closed straight away, so an old
        // alert never shows up open on the map for the seconds between its start and end being processed.
        var finishedAt = payload.FinishedAt;
        if (kind == "alert.started")
        {
            if (existing is null && place is not null)
            {
                db.AirAlerts.Add(new AirAlert
                {
                    SourceId = source.SourceId,
                    SourceAlertId = sourceAlertId,
                    PlaceId = place.PlaceId,
                    AlertType = alertType,
                    Level = level,
                    StartedAt = startedAt,
                    StartRawMessageId = raw.RawMessageId,
                    EndedAt = finishedAt,
                });
            }
            else if (existing is not null)
            {
                existing.StartRawMessageId ??= raw.RawMessageId; // the end came first
                if (existing.EndedAt is null && existing.Level != level && level != AirAlertLevel.Unknown)
                {
                    existing.Level = level; // the same alert re-announced with a new level
                }
            }
        }
        else if (existing is not null)
        {
            if (existing.EndedAt is null)
            {
                existing.EndedAt = at;
            }
            existing.EndRawMessageId ??= raw.RawMessageId;
        }
        else if (place is not null)
        {
            // End before start: the payload carries the whole alert, so the interval is complete without the start message.
            db.AirAlerts.Add(new AirAlert
            {
                SourceId = source.SourceId,
                SourceAlertId = sourceAlertId,
                PlaceId = place.PlaceId,
                AlertType = alertType,
                Level = level,
                StartedAt = startedAt,
                EndedAt = at,
                EndRawMessageId = raw.RawMessageId,
            });
        }

        var obs = new Target
        {
            RawMessageId = raw.RawMessageId,
            SourceId = source.SourceId,
            SegmentIndex = 0,
            SegmentText = $"{(kind == "alert.started" ? "Тривога" : "Відбій")}: {title} ({Str(alert, "alert_type")})",
            ObservedAt = at,
            EventType = kind == "alert.started" ? EventType.AirRaidAlert : EventType.AlertCancelled,
            AlertLevel = level,
            IdentificationMethod = IdentificationMethod.Structured,
            IdentificationSource = "alerts.in.ua",
            ParserVersion = Version,
            Confidence = ConfidenceLevel.Confirmed,
            LocationKind = place is null ? LocationKind.Unknown : Pipeline.TargetBuilder.KindFor(place.Level),
            LocationPlaceId = place?.PlaceId,
            Location = place?.Centroid,
            LocationAccuracyKm = place?.RadiusKm,
            DirectionKind = DirectionKind.Unknown,
            ParserMetadata = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                kind,
                alertType = Str(alert, "alert_type"),
                alertLevel = Str(alert, "alert_level"),
                locationType,
                title,
                raion,
                oblast,
                placeId = place?.PlaceId,
                placeLevel = place?.Level.ToString(),
                sourceAlertId,
            })),
        };
        return [obs];
    }

    public static PlaceLevel? LevelOf(string locationType) => locationType switch
    {
        "oblast" => PlaceLevel.Region,
        "raion" => PlaceLevel.District,
        "hromada" => PlaceLevel.Hromada,
        _ => null,
    };

    /// <summary>
    /// From the most precise level the feed names down to the oblast: hromada → raion → oblast, city → its hromada
    /// polygon (the settlement lies inside it) → the settlement itself → oblast.
    /// </summary>
    public PlaceEntry? ResolvePlace(string locationType, string title, string? raion, string oblast)
    {
        var gazetteer = indexes.Gazetteer;
        var region = ResolveRegion(oblast) ?? ResolveRegion(title);
        switch (locationType)
        {
            case "oblast":
                return region;
            case "raion":
                return gazetteer.FindAdmin(PlaceLevel.District, title, region?.PlaceId) ?? region;
            case "hromada":
                return gazetteer.FindAdmin(PlaceLevel.Hromada, title, region?.PlaceId)
                    ?? (raion is null ? null : gazetteer.FindAdmin(PlaceLevel.District, raion, region?.PlaceId))
                    ?? region;
            case "city":
            {
                var city = ResolveSettlement(title, region);
                if (city is not null)
                {
                    return gazetteer.PolygonAt(city.Centroid.Coordinate, PlaceLevel.Hromada) ?? city;
                }
                return (raion is null ? null : gazetteer.FindAdmin(PlaceLevel.District, raion, region?.PlaceId)) ?? region;
            }
            default:
                return region;
        }
    }

    private PlaceEntry? ResolveRegion(string title)
    {
        return Match(title)
            .Where(p => p.Level is PlaceLevel.Region or PlaceLevel.City && p.ParentId is null)
            .FirstOrDefault();
    }

    /// <summary>"м. Запоріжжя" → the city; the biggest settlement of that name inside the oblast wins.</summary>
    private PlaceEntry? ResolveSettlement(string title, PlaceEntry? region)
    {
        var name = title.Trim();
        foreach (var prefix in new[] { "м. ", "м.", "місто ", "смт ", "с. " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..].Trim();
                break;
            }
        }
        var candidates = Match(name).Where(p => p.Level is PlaceLevel.City or PlaceLevel.Town or PlaceLevel.Village).ToList();
        if (region is not null)
        {
            var inside = candidates.Where(p => indexes.Gazetteer.RegionOf(p)?.PlaceId == region.PlaceId).ToList();
            if (inside.Count > 0)
            {
                candidates = inside;
            }
        }
        return candidates.OrderByDescending(p => p.Population).FirstOrDefault();
    }

    private IEnumerable<PlaceEntry> Match(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var normalized = normalizer.Normalize(text);
        if (normalized.Segments.Count == 0)
        {
            return [];
        }
        var matcher = new PlaceMatcher(indexes.Gazetteer);
        var ctx = new ParseContext(0, "uk", null);
        return matcher.Match(normalized.Segments[0], ctx, new HashSet<int>(), new HashSet<(int, int)>()).Select(m => m.Place);
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
