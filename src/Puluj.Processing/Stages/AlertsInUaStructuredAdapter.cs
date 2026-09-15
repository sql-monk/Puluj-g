using System.Text.Json;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Structured;

namespace Puluj.Processing.Stages;

/// <summary>
/// Pure structured extraction for alerts.in.ua payloads (plan §4: «structured adapter не змінює alerts»): the same
/// recognition as <see cref="AlertsInUaHandler"/> — place from the level the feed names, alert type, level, times —
/// returned as an in-memory <see cref="Target"/> for the fact mapper, without reading or writing `air_alerts`.
/// The alert interval itself is the alert worker's state (P09); the fact carries `started_at`/`finished_at` so an end
/// that arrives before its start can still be applied there.
/// </summary>
public sealed class AlertsInUaStructuredAdapter(AlertsInUaHandler handler)
{
    public const string Version = "alerts_in_ua-adapter-1";

    public static bool CanHandle(RawMessage raw) => AlertsInUaHandler.CanHandle(raw);

    /// <summary>`structured_kind` of `message.normalized`: `alerts_in_ua.alert.started` / `alerts_in_ua.alert.finished`.</summary>
    public static string StructuredKind(RawMessage raw) => $"alerts_in_ua.{raw.RawPayload!.RootElement.GetProperty("kind").GetString()}";

    public Target Extract(RawMessage raw, Source source)
    {
        var payload = AlertsInUaHandler.ParsePayload(raw.RawPayload!.RootElement, raw.PublishedAt);
        var place = handler.ResolvePlace(payload.LocationType, payload.Title, payload.Raion, payload.Oblast);
        return new Target
        {
            RawMessageId = raw.RawMessageId,
            SourceId = source.SourceId,
            SegmentIndex = 0,
            SegmentText = $"{(payload.IsStart ? "Тривога" : "Відбій")}: {payload.Title} ({payload.AlertTypeText})",
            ObservedAt = payload.At,
            EventType = payload.IsStart ? EventType.AirRaidAlert : EventType.AlertCancelled,
            AlertLevel = payload.Level,
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
                kind = payload.Kind,
                alertType = payload.AlertTypeText,
                alertLevel = payload.LevelText,
                locationType = payload.LocationType,
                title = payload.Title,
                raion = payload.Raion,
                oblast = payload.Oblast,
                placeId = place?.PlaceId,
                placeLevel = place?.Level.ToString(),
                placeResolved = place is not null,
                sourceAlertId = payload.SourceAlertId,
                startedAt = payload.StartedAt.UtcDateTime.ToString("O"),
                finishedAt = payload.FinishedAt?.UtcDateTime.ToString("O"),
            })),
        };
    }
}
