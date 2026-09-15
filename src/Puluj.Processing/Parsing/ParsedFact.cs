using Puluj.Domain.Enums;
using Puluj.Processing.Indexes;

namespace Puluj.Processing.Parsing;

public enum PlaceRole
{
    Current,
    Origin,
    Destination,
    Transit,
}

public sealed record TargetMention(TargetRef Ref, string MatchedText, ConfidenceLevel ImpliedConfidence, bool Hedged, int TokenIndex, int TokenCount)
{
    /// <summary>Confidence after applying hedges ("ймовірно") — never above the alias itself.</summary>
    public ConfidenceLevel EffectiveConfidence => Hedged
        ? (ConfidenceLevel)Math.Max((int)ConfidenceLevel.Low, (int)ImpliedConfidence - 1)
        : ImpliedConfidence;
}

/// <param name="QuadrantDeg">"на півночі Київщини": which part of the area was named, as a bearing from its centre; null when the whole area.</param>
public sealed record PlaceMention(PlaceEntry Place, PlaceRole Role, string MatchedText, int TokenIndex, int TokenCount, int Score, double? QuadrantDeg = null);

public sealed record DirectionMention(double Degrees, DirectionKind Kind, string Text);

/// <summary>One fact extracted from one text segment. Everything a downstream stage needs to build an Target.</summary>
public sealed record ParsedFact
{
    public required int SegmentIndex { get; init; }
    public required string SegmentText { get; init; }
    public required EventType EventType { get; init; }
    public TargetMention? Target { get; init; }
    public int? Count { get; init; }
    public bool CountIsApproximate { get; init; }
    public IReadOnlyList<PlaceMention> Places { get; init; } = [];
    public DirectionMention? Direction { get; init; }
    public bool IsLaunch { get; init; }
    /// <summary>Stated alert level for AirRaidAlert facts ("жовтий рівень"); Unknown otherwise.</summary>
    public AirAlertLevel AlertLevel { get; init; }
    /// <summary>Rule names that fired, for ParserMetadata.</summary>
    public IReadOnlyList<string> Rules { get; init; } = [];
    public IdentificationMethod Method { get; init; } = IdentificationMethod.Rule;
    /// <summary>Parser that produced the fact (RuleParser.Version or the LLM model + prompt version).</summary>
    public string ParserVersion { get; init; } = RuleParser.Version;

    /// <summary>The current position: among the places named as current, the most specific one ("Київ: ... над Оболонським районом"
    /// is the district, "Київщина: БпЛА біля Броварів" is the town), then the first one, then a transit place.</summary>
    public PlaceMention? Current
    {
        get
        {
            var current = Places.Where(p => p.Role == PlaceRole.Current).ToList();
            return current.FirstOrDefault(p => current.Any(q => q.Place.PlaceId == p.Place.ParentId))
                ?? current.FirstOrDefault()
                ?? Places.FirstOrDefault(p => p.Role == PlaceRole.Transit);
        }
    }
    public PlaceMention? Origin => Places.FirstOrDefault(p => p.Role == PlaceRole.Origin);
    public PlaceMention? Destination => Places.FirstOrDefault(p => p.Role == PlaceRole.Destination);
}

/// <param name="PublishedAt">When the message was published; the LLM fallback skips messages older than Llm:MaxMessageAgeHours.</param>
public sealed record ParseContext(int SourceId, string Language, int? HomeRegionPlaceId, DateTimeOffset? PublishedAt = null, long? RawMessageId = null);

public interface IParser
{
    /// <summary>Parses the whole message; returns zero or more facts. Never throws on unexpected text.</summary>
    Task<IReadOnlyList<ParsedFact>> ParseAsync(Text.NormalizedMessage message, ParseContext context, CancellationToken ct);
}
