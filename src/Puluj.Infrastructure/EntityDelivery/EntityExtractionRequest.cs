using System.Text.Json;

namespace Puluj.Infrastructure.EntityExtraction;

public sealed record EntityExtractionRequest(
    Guid DeliveryId,
    long RawMessageId,
    int SourceId,
    string SourceCode,
    DateTimeOffset PublishedAt,
    DateTimeOffset ReceivedAt,
    string? Text,
    JsonElement? RawPayload,
    string? Url);

public sealed record ClaimedEntityDelivery(long AttemptId, DateTimeOffset ClaimedAt, EntityExtractionRequest Request);
