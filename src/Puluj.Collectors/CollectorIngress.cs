using System.Text.Json;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Ingestion;

namespace Puluj.Collectors;

public sealed record CollectorCheckpoint(string? LastSourceMessageId = null, DateTimeOffset? LastMessageAt = null, JsonDocument? Cursor = null);

/// <summary>Result of storing a collector message in PostgreSQL.</summary>
public sealed record IngressResult(bool Accepted, long? RawMessageId, bool? IsNew)
{
    /// <summary>True unless the store reported an existing row (unknown novelty counts as accepted).</summary>
    public bool Stored => Accepted && IsNew != false;
}

/// <summary>
/// The one entry point of the collectors. A collector writes the original message directly to PostgreSQL and then
/// advances its checkpoint. Processor replicas claim pending raw_messages independently.
/// </summary>
public sealed class CollectorIngress(RawMessageIngestor ingestor, CollectorStateStore states)
{
    /// <param name="checkpoint">Source cursor to write after the message is stored (null = leave it).</param>
    /// <param name="live">False during a history load: store without waking processors until the ordered load is complete.</param>
    public async Task<IngressResult> PublishAsync(IncomingMessage msg, Source source, string collectorName, CollectorCheckpoint? checkpoint, bool live, CancellationToken ct)
    {
        var result = await ingestor.IngestAsync(msg, source.Code, ct, announceProcessor: live);
        if (checkpoint is not null)
        {
            await states.MarkSuccessAsync(source.SourceId, checkpoint.LastSourceMessageId, checkpoint.LastMessageAt, checkpoint.Cursor, ct);
        }
        return new IngressResult(true, result.RawMessageId, result.IsNew);
    }
}
