using Microsoft.Extensions.Options;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Tests;

public class CopyDetectorTests
{
    private static readonly CopyDetector Detector = new(Options.Create(new AnalyticsOptions()));
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static MessageFingerprint Message(long id, int source, string key, DateTimeOffset at, int? forwardedFrom = null) =>
        new() { RawMessageId = id, SourceId = source, PostKey = key, PublishedAt = at, ForwardedSourceId = forwardedFrom };

    private static Candidate Cand(long id, int source, string key, DateTimeOffset at, int? forwardedFrom = null) =>
        new(id, source, key, at.UtcDateTime, forwardedFrom);

    private static EventFact Fact(long messageId, int eventType = 1, int category = 1, int place = 10, int destination = 20, int? count = 3, double? direction = 270) =>
        new(messageId, T0.UtcDateTime, eventType, category, 1, 1, null, count, false, place, null, destination, direction);

    [Fact]
    public void Post_key_strips_edit_suffix()
    {
        Assert.Equal(("87675", true), new RawRow(1, 7, "87675:e1789433701", default, default, null, null, null).Post());
        Assert.Equal(("87675", false), new RawRow(1, 7, "87675", default, default, null, null, null).Post());
        Assert.Equal(("12:start", false), new RawRow(1, 1, "12:start", default, default, null, null, null).Post());
    }

    [Fact]
    public void Forwarded_channel_id_is_parsed_from_channel_form_only()
    {
        Assert.Equal(1463721328, RawMessageReader.ForwardedChannelId("channel 1463721328"));
        Assert.Null(RawMessageReader.ForwardedChannelId("Приймальня єРадару"));
        Assert.Null(RawMessageReader.ForwardedChannelId(null));
    }

    [Fact]
    public void Earlier_post_is_the_original_whichever_side_was_indexed_first()
    {
        var newer = Message(20, 2, "20", T0.AddMinutes(5));
        var older = Cand(10, 3, "10", T0);
        var pair = Detector.Pair(newer, new Match(older, 0.95, 1, IsSemantic: true), T0.AddHours(1));
        Assert.Equal((3, "10", 2, "20"), (pair.OriginalSourceId, pair.OriginalPostKey, pair.CopySourceId, pair.CopyPostKey));
        Assert.Equal(300, pair.DelaySeconds);
        Assert.Equal(CopyKind.Near, pair.Kind);

        // The history load brings the original in after the copy was indexed: the new message becomes the original.
        var late = Message(30, 3, "30", T0.AddMinutes(-10));
        var indexedCopy = Cand(20, 2, "20", T0.AddMinutes(5));
        var reversed = Detector.Pair(late, new Match(indexedCopy, 0.7, 0.8, IsSemantic: true), T0.AddHours(1));
        Assert.Equal((3, "30", 2, "20"), (reversed.OriginalSourceId, reversed.OriginalPostKey, reversed.CopySourceId, reversed.CopyPostKey));
        Assert.Equal(900, reversed.DelaySeconds);
        Assert.Equal(CopyKind.Near, reversed.Kind);
    }

    [Fact]
    public void Same_instant_orders_by_id()
    {
        var a = Message(20, 2, "20", T0);
        var b = Cand(10, 3, "10", T0);
        var pair = Detector.Pair(a, new Match(b, 1, 1, IsSemantic: true), T0);
        Assert.Equal(3, pair.OriginalSourceId);
        Assert.Equal(0, pair.DelaySeconds);
    }

    [Fact]
    public void Forward_from_the_original_source_is_a_forward()
    {
        var copy = Message(20, 2, "20", T0.AddMinutes(1), forwardedFrom: 3);
        var original = Cand(10, 3, "10", T0);
        Assert.Equal(CopyKind.Forward, Detector.Pair(copy, new Match(original, 0.5, 0.6, IsSemantic: true), T0).Kind);
        // Forwarded from some other source: not a forward of this original.
        var other = Message(21, 2, "21", T0.AddMinutes(1), forwardedFrom: 6);
        Assert.Equal(CopyKind.Near, Detector.Pair(other, new Match(original, 0.5, 0.6, IsSemantic: true), T0).Kind);
    }

    [Fact]
    public void Different_wording_of_the_same_observation_matches_by_event_features()
    {
        // "Три шахеди над Сумами курсом на Полтаву" and
        // "Група БпЛА з Сумщини рухається на Полтавщину" can have no common text shingles.
        var first = Fact(1);
        var paraphrase = Fact(2);

        Assert.True(SemanticEventMatcher.Matches(first, paraphrase, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Similar_template_about_another_event_is_not_a_semantic_match()
    {
        // The sentence template may be identical, but a different target count, place, phase or direction is a
        // different event and must not be credited as a cross-channel copy.
        Assert.False(SemanticEventMatcher.Matches(Fact(1), Fact(2, count: 8), TimeSpan.FromHours(1)));
        Assert.False(SemanticEventMatcher.Matches(Fact(1), Fact(2, place: 11, destination: 21), TimeSpan.FromHours(1)));
        Assert.False(SemanticEventMatcher.Matches(Fact(1), Fact(2, eventType: 20), TimeSpan.FromHours(1)));
        Assert.False(SemanticEventMatcher.Matches(Fact(1), Fact(2, direction: 90), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Semantic_pair_kind_does_not_depend_on_text_similarity()
    {
        Assert.Equal(CopyKind.Near, Detector.KindOf(null, originalSourceId: 3));
        Assert.Equal(CopyKind.Forward, Detector.KindOf(copyForwardedSourceId: 3, originalSourceId: 3));
    }
}
