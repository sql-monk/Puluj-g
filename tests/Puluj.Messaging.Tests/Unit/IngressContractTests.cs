using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>`ingress.received` of the collectors (P04) against the envelope + payload contract; identity per source type (ADR-0003).</summary>
public sealed class IngressContractTests
{
    private static readonly JsonSchema EnvelopeSchema = ContractSchemas.Envelope;
    private static readonly JsonSchema IngressSchema = ContractSchemas.Ingress;
    private static readonly EvaluationOptions Options = ContractSchemas.Options;
    private static readonly DateTimeOffset At = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static string Describe(EvaluationResults results) =>
        string.Join("; ", (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct());

    private static JsonNode Build(IncomingMessage msg, string sourceCode, string collector, CollectorCheckpoint? checkpoint = null, string lane = "live")
    {
        var (envelope, _, _) = IngressWriter.BuildEnvelope(msg, sourceCode, collector, checkpoint, lane, Guid.CreateVersion7(), "2026.09.15+d1bf1b0", "collectors@collector-telegram", "collector-telegram", At.AddSeconds(1));
        envelope.EventId = Guid.CreateVersion7(); // what the outbox writer assigns
        envelope.PublishedAt = At.AddSeconds(2);
        envelope.TopologyVersion = 3;
        return JsonNode.Parse(envelope.ToJson())!;
    }

    [Fact]
    public void Telegram_post_and_edit_are_valid_root_events_sharing_the_correlation()
    {
        var payload = JsonDocument.Parse("{\"id\":48213,\"date\":1789466380,\"channel\":\"monitoringwar\"}");
        var post = Build(new IncomingMessage { SourceId = 3, SourceMessageId = "48213", SourceMessageKey = "48213", SourceRevision = "0", PublishedAt = At, RawText = "Шахеди курсом на Бровари", RawPayload = payload, Url = "https://t.me/monitoringwar/48213" },
            "tg_monitoringwar", "telegram", new CollectorCheckpoint("48213", At));
        var edit = Build(new IncomingMessage { SourceId = 3, SourceMessageId = "48213:e1789466500", SourceMessageKey = "48213", SourceRevision = "e1789466500", PublishedAt = At.AddMinutes(2), RawText = "Шахеди курсом на Бровари. UPD: відбій", RawPayload = payload },
            "tg_monitoringwar", "telegram", new CollectorCheckpoint(null, At.AddMinutes(2)));
        foreach (var json in new[] { post, edit })
        {
            var result = EnvelopeSchema.Evaluate(ToElement(json), Options);
            Assert.True(result.IsValid, Describe(result));
            var payloadResult = IngressSchema.Evaluate(ToElement(json["payload"]!), Options);
            Assert.True(payloadResult.IsValid, Describe(payloadResult));
            Assert.Equal("ingress.received", json["event_type"]!.GetValue<string>());
            Assert.True(json.AsObject().ContainsKey("causation_id") && json["causation_id"] is null); // root: present and null (envelope schema)
            Assert.Null(json["raw_message_id"]);
            Assert.Equal(64, json["payload"]!["content_hash"]!.GetValue<string>().Length);
            Assert.Equal("telegram", json["payload"]!["collector"]!["name"]!.GetValue<string>());
        }
        Assert.Equal(post["correlation_id"]!.GetValue<string>(), edit["correlation_id"]!.GetValue<string>());
        Assert.Equal("48213", edit["source_message_key"]!.GetValue<string>());
        Assert.Equal("e1789466500", edit["source_revision"]!.GetValue<string>());
        Assert.Equal("48213:e1789466500", edit["payload"]!["legacy_source_message_id"]!.GetValue<string>());
        Assert.Equal("48213", post["payload"]!["checkpoint"]!["last_source_message_id"]!.GetValue<string>());
        Assert.Null(edit["payload"]!["checkpoint"]!["last_source_message_id"]); // an edit does not move the id checkpoint
    }

    [Fact]
    public void Alerts_start_and_end_are_different_keys_with_revision_zero_and_history_lane_is_kept()
    {
        var alert = JsonDocument.Parse("{\"kind\":\"alert.started\",\"at\":\"2026-09-15T09:58:00Z\",\"alert\":{\"id\":31,\"location_uid\":\"12\"}}");
        var start = Build(new IncomingMessage { SourceId = 7, SourceMessageId = "31:start", SourceMessageKey = "31:start", SourceRevision = "0", PublishedAt = At, RawPayload = alert, Url = "https://alerts.in.ua/" }, "alerts_in_ua", "alerts.in.ua");
        var end = Build(new IncomingMessage { SourceId = 7, SourceMessageId = "31:end", PublishedAt = At.AddMinutes(40), RawPayload = alert }, "alerts_in_ua", "alerts.in.ua history", lane: "history");
        foreach (var json in new[] { start, end })
        {
            Assert.True(EnvelopeSchema.Evaluate(ToElement(json), Options).IsValid, Describe(EnvelopeSchema.Evaluate(ToElement(json), Options)));
            Assert.True(IngressSchema.Evaluate(ToElement(json["payload"]!), Options).IsValid, Describe(IngressSchema.Evaluate(ToElement(json["payload"]!), Options)));
            Assert.Equal("0", json["source_revision"]!.GetValue<string>());
            Assert.Null(json["payload"]!["text"]); // payload-only message satisfies anyOf through raw_payload
        }
        Assert.NotEqual(start["correlation_id"]!.GetValue<string>(), end["correlation_id"]!.GetValue<string>()); // start/end are different source messages
        Assert.Equal("31:end", end["source_message_key"]!.GetValue<string>()); // derived from the legacy id when not given
        Assert.Equal("history", end["lane"]!.GetValue<string>());
    }

    [Fact]
    public void Content_hash_matches_the_direct_store_and_identity_defaults_follow_the_legacy_id()
    {
        var payload = JsonDocument.Parse("{ \"b\": 1,  \"a\": [1, 2] }"); // spacing and key order survive: the hash is over the original text
        var msg = new IncomingMessage { SourceId = 3, SourceMessageId = "77:e1700000000", PublishedAt = At, RawText = "text", RawPayload = payload };
        var (_, identity, hash) = IngressWriter.BuildEnvelope(msg, "tg_x", "telegram", null, "live", Guid.CreateVersion7(), "v", "collectors@i", "i", At);
        Assert.Equal(RawMessageIngestor.ComputeHash("tg_x", "text", payload.RootElement.GetRawText()), hash);
        Assert.Equal(new SourceIdentity("77", "e1700000000"), identity);
        Assert.Equal(identity, RawMessageIngestor.Identity(msg));
    }
}
