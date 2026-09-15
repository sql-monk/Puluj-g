using Microsoft.Extensions.Logging;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Collectors;

/// <summary>What a collector learns synchronously: through the ingress the raw id and novelty are only known once the raw-writer has run.</summary>
public sealed record IngressResult(bool Accepted, long? RawMessageId, bool? IsNew)
{
    /// <summary>True unless the store reported an existing row (unknown novelty counts as accepted).</summary>
    public bool Stored => Accepted && IsNew != false;
}

/// <summary>
/// The one entry point of the collectors (plan §6.1). `Messaging:Ingress:Enabled` chooses the path: the durable
/// producer outbox (`ingress.received` + checkpoint in one transaction; the raw-writer stores the row) or the legacy
/// direct store (`RawMessageIngestor`, optionally with the P03 bridge) followed by a separate checkpoint write.
/// Collectors never see the difference beyond <see cref="IngressResult"/>.
/// </summary>
public sealed class CollectorIngress(RawMessageIngestor ingestor, IngressWriter writer, CollectorStateStore states, ILogger<CollectorIngress> logger)
{
    public bool IngressEnabled => writer.Enabled;

    /// <param name="checkpoint">Source cursor to commit with the message (null = leave it); in the direct path it is written right after the store.</param>
    /// <param name="live">False during a history load: lane `history`, no processing wake-up.</param>
    public async Task<IngressResult> PublishAsync(IncomingMessage msg, Source source, string collectorName, CollectorCheckpoint? checkpoint, bool live, CancellationToken ct)
    {
        if (IngressEnabled)
        {
            await writer.PublishAsync(msg, source.Code, collectorName, checkpoint, live, ct);
            return new IngressResult(true, null, null);
        }
        var result = await ingestor.IngestAsync(msg, source.Code, ct, enqueue: live);
        if (checkpoint is not null)
        {
            await states.MarkSuccessAsync(source.SourceId, checkpoint.LastSourceMessageId, checkpoint.LastMessageAt, checkpoint.Cursor, ct);
        }
        return new IngressResult(true, result.RawMessageId, result.IsNew);
    }

    /// <summary>Before a rebuild: every published ingress of these sources has its raw row (true immediately on the direct path).</summary>
    public async Task<bool> WaitForDrainAsync(IReadOnlyCollection<int> sourceIds, CancellationToken ct)
    {
        if (!IngressEnabled)
        {
            return true;
        }
        var drained = await writer.WaitForDrainAsync(sourceIds, null, ct);
        if (!drained)
        {
            logger.LogError("History load continues without a full drain: raw rows still being written by the raw-writer may be processed out of order");
        }
        return drained;
    }
}
