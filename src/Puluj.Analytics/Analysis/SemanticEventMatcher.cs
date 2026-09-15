namespace Puluj.Analytics.Analysis;

/// <summary>
/// A parsed, observable fact used to decide whether posts from different channels describe the same event.
/// Text is deliberately not part of this value: wording is evidence only after the event has been identified.
/// </summary>
public sealed record EventFact(
    long RawMessageId,
    DateTime ObservedAt,
    int EventType,
    int? TargetCategoryId,
    int? TargetClassId,
    int? TargetFamilyId,
    int? TargetModelId,
    int? ObjectCount,
    bool ObjectCountIsApproximate,
    int? LocationPlaceId,
    int? OriginPlaceId,
    int? DestinationPlaceId,
    double? DirectionDeg);

/// <summary>Semantic comparison of two parsed facts. A match needs the same event and target kind at the same named place;
/// model/family, time, direction and count then guard against coincidental reports of different objects.</summary>
public static class SemanticEventMatcher
{
    private const double MaxDirectionDifference = 75;

    public static bool Matches(EventFact left, EventFact right, TimeSpan window)
    {
        if (left.EventType != right.EventType
            || left.TargetCategoryId != right.TargetCategoryId
            || Math.Abs((left.ObservedAt - right.ObservedAt).TotalMinutes) > window.TotalMinutes
            || !Compatible(left.TargetClassId, right.TargetClassId)
            || !Compatible(left.TargetFamilyId, right.TargetFamilyId)
            || !Compatible(left.TargetModelId, right.TargetModelId)
            || !SamePlace(left, right)
            || !CompatibleCount(left, right))
        {
            return false;
        }

        return left.DirectionDeg is not double a || right.DirectionDeg is not double b
               || AngleDifference(a, b) <= MaxDirectionDifference;
    }

    private static bool Compatible(int? left, int? right) => left is null || right is null || left == right;

    private static bool CompatibleCount(EventFact left, EventFact right) =>
        left.ObjectCount is null || right.ObjectCount is null || left.ObjectCountIsApproximate || right.ObjectCountIsApproximate
            || left.ObjectCount == right.ObjectCount;

    private static bool SamePlace(EventFact left, EventFact right)
    {
        var places = Places(left).ToHashSet();
        return places.Count > 0 && Places(right).Any(places.Contains);
    }

    private static IEnumerable<int> Places(EventFact fact)
    {
        if (fact.LocationPlaceId is int location) yield return location;
        if (fact.OriginPlaceId is int origin) yield return origin;
        if (fact.DestinationPlaceId is int destination) yield return destination;
    }

    private static double AngleDifference(double a, double b)
    {
        var difference = Math.Abs((a - b) % 360);
        return difference > 180 ? 360 - difference : difference;
    }
}
