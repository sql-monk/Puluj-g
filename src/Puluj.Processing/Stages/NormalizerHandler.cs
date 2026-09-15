using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Text;

namespace Puluj.Processing.Stages;

/// <summary>
/// The `normalizer` subscription (plan §4): `raw.stored` → `message.normalized`. Classifies the original (text /
/// structured / empty), normalises the text and records the stage in `processing.stage_results`; the normalisation
/// runs outside the transaction, the transaction only inserts the stage row and the outgoing event. A repeat of the
/// same raw in the same run (a second `raw.stored{is_new:false}`, a replayed upstream event) finds the stage row
/// already there and completes as `noop` without publishing again.
/// </summary>
public sealed class NormalizerHandler(IDbContextFactory<PulujDbContext> factory, INormalizer normalizer, TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "normalizer";
    public const string Stage = "normalize";
    public const string EventType = "message.normalized";
    public const string SchemaVersion = "1.0";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Prepared(RawMessage Raw, string TextKind, string? StructuredKind, NormalizedMessage? Normalized, string? Hash, DateTimeOffset StartedAt);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "raw.stored without raw_message_id");
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found (transient: the raw-writer commit may not be visible yet)");
        if (AlertsInUaStructuredAdapter.CanHandle(raw))
        {
            return new Prepared(raw, "structured", AlertsInUaStructuredAdapter.StructuredKind(raw), null, null, startedAt);
        }
        if (!string.IsNullOrWhiteSpace(raw.RawText))
        {
            var normalized = normalizer.Normalize(raw.RawText);
            return new Prepared(raw, "text", null, normalized, StageSupport.Sha256(normalized.Text), startedAt);
        }
        return new Prepared(raw, "empty", null, null, null, startedAt);
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var p = (Prepared)state!;
        var eventId = Guid.CreateVersion7();
        var outputs = new JsonObject
        {
            ["message_normalized_event_id"] = eventId.ToString(),
            ["text_kind"] = p.TextKind,
            ["structured_kind"] = p.StructuredKind,
            ["language"] = p.Normalized?.Language,
            ["segments"] = p.Normalized?.Segments.Count,
            ["normalized_text_hash"] = p.Hash,
            ["caused_by"] = envelope.EventId.ToString(),
        };
        var versions = new JsonObject { ["normalization"] = Normalizer.Version };
        var stageId = await StageSupport.InsertStageResultAsync(conn, tx, p.Raw.RawMessageId, envelope.ProcessingRunId, Stage, Normalizer.Version, "completed", outputs, versions, p.StartedAt, Producer, ct);
        if (stageId is null)
        {
            return DeliveryResult.Noop("stage already recorded for this raw/run");
        }
        var payload = new JsonObject
        {
            ["raw_message_id"] = p.Raw.RawMessageId,
            ["normalization_version"] = Normalizer.Version,
            ["text_kind"] = p.TextKind,
        };
        if (p.StructuredKind is not null)
        {
            payload["structured_kind"] = p.StructuredKind;
        }
        if (p.Normalized is not null)
        {
            payload["language"] = p.Normalized.Language;
            payload["normalized_text"] = p.Normalized.Text;
            payload["normalized_text_hash"] = p.Hash;
        }
        var outgoing = StageSupport.Child(envelope, EventType, SchemaVersion, Producer, clock.GetUtcNow(), payload);
        outgoing.EventId = eventId;
        return new DeliveryResult("completed", null, [outgoing]) { StageResultId = stageId };
    }
}
