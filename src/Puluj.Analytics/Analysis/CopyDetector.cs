using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Text;

namespace Puluj.Analytics.Analysis;

/// <summary>An indexed post from another source selected as a possible match for the same parsed event.</summary>
public sealed record Candidate(long RawMessageId, int SourceId, string PostKey, DateTime PublishedAt, int? ForwardedSourceId);

/// <summary>What the exact check decided about one candidate.</summary>
public sealed record Match(Candidate Other, double Jaccard, double Containment, bool IsSemantic = false);

/// <summary>
/// Finds posts from other sources about the same parsed event. Event type, target kind, place, time, direction and
/// count decide the relation; text similarity is recorded as supporting evidence only. A message without parsed facts
/// is intentionally not paired: identical boilerplate is not evidence that two channels saw the same event.
/// </summary>
public sealed class CopyDetector(IOptions<AnalyticsOptions> options)
{
    private AnalyticsOptions O => options.Value;

    public async Task<List<Match>> FindAsync(AnalyticsDbContext db, MessageFingerprint message, TextFingerprint fingerprint, CancellationToken ct)
    {
        var facts = await RawMessageReader.FactsAsync(db, [message.RawMessageId], ct);
        if (facts.Count > 0)
        {
            return await FindSemanticAsync(db, message, fingerprint, facts, ct);
        }
        return [];
    }

    private async Task<List<Match>> FindSemanticAsync(AnalyticsDbContext db, MessageFingerprint message, TextFingerprint fingerprint, List<EventFact> facts, CancellationToken ct)
    {
        var candidates = new Dictionary<long, Candidate>();
        foreach (var fact in facts.Select(f => (f.EventType, f.TargetCategoryId)).Distinct())
        {
            var representative = facts.First(f => f.EventType == fact.EventType && f.TargetCategoryId == fact.TargetCategoryId);
            foreach (var candidate in await RawMessageReader.SemanticCandidatesAsync(db, message, representative, O.EventWindow, O.CandidateScan, ct))
            {
                candidates[candidate.RawMessageId] = candidate;
            }
        }
        if (candidates.Count == 0)
        {
            return [];
        }

        var otherFacts = (await RawMessageReader.FactsAsync(db, candidates.Keys.ToArray(), ct))
            .GroupBy(f => f.RawMessageId).ToDictionary(g => g.Key, g => g.ToList());
        var texts = (await RawMessageReader.TextsAsync(db, candidates.Keys.ToArray(), ct))
            .ToDictionary(x => x.RawMessageId, x => x.Text);
        var matches = new List<Match>();
        foreach (var (id, candidate) in candidates)
        {
            if (!otherFacts.TryGetValue(id, out var theirs)
                || !facts.Any(ours => theirs.Any(theirsFact => SemanticEventMatcher.Matches(ours, theirsFact, O.EventWindow))))
            {
                continue;
            }
            var canonical = TextNormalizer.Canonical(texts.GetValueOrDefault(id));
            var other = Shingler.Shingles(canonical);
            matches.Add(new Match(candidate, Shingler.Jaccard(fingerprint.Shingles, other), Shingler.Containment(fingerprint.Shingles, other), IsSemantic: true));
        }
        return matches;
    }

    /// <summary>The pair as it is stored: the earlier post is the original. Same instant — the smaller id was stored first.</summary>
    public MessageCopy Pair(MessageFingerprint message, Match match, DateTimeOffset now)
    {
        var other = match.Other;
        var otherPublished = new DateTimeOffset(DateTime.SpecifyKind(other.PublishedAt, DateTimeKind.Utc));
        var messageIsOriginal = message.PublishedAt < otherPublished || (message.PublishedAt == otherPublished && message.RawMessageId < other.RawMessageId);
        var (copySource, copyKey, copyId, copyAt, copyForward) = messageIsOriginal
            ? (other.SourceId, other.PostKey, other.RawMessageId, otherPublished, other.ForwardedSourceId)
            : (message.SourceId, message.PostKey, message.RawMessageId, message.PublishedAt, message.ForwardedSourceId);
        var (origSource, origKey, origId, origAt) = messageIsOriginal
            ? (message.SourceId, message.PostKey, message.RawMessageId, message.PublishedAt)
            : (other.SourceId, other.PostKey, other.RawMessageId, otherPublished);
        return new MessageCopy
        {
            CopySourceId = copySource,
            CopyPostKey = copyKey,
            OriginalSourceId = origSource,
            OriginalPostKey = origKey,
            CopyRawMessageId = copyId,
            OriginalRawMessageId = origId,
            CopyPublishedAt = copyAt,
            OriginalPublishedAt = origAt,
            DelaySeconds = (copyAt - origAt).TotalSeconds,
            Jaccard = (float)match.Jaccard,
            Containment = (float)match.Containment,
            Kind = KindOf(copyForward, origSource),
            IsPrimary = false,
            FoundAt = now,
        };
    }

    public CopyKind KindOf(int? copyForwardedSourceId, int originalSourceId) =>
        copyForwardedSourceId == originalSourceId ? CopyKind.Forward
        : CopyKind.Near;
}
