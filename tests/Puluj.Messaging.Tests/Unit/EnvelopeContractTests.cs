using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>
/// The bridge envelope against the contract (§16.2 п.1: no drift between producer and schema): envelope.schema.json +
/// raw.stored.schema.json validate what <see cref="RawStoredEnvelope"/> + the outbox writer produce; identity follows
/// fixtures/identity-cases.json; parsing keeps unknown additive fields for the archive.
/// </summary>
public sealed class EnvelopeContractTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "messaging");
    private static readonly JsonSchema EnvelopeSchema;
    private static readonly JsonSchema RawStoredSchema;
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

    static EnvelopeContractTests()
    {
        JsonSchema.FromFile(Path.Combine(Root, "schemas", "common.schema.json"));
        EnvelopeSchema = JsonSchema.FromFile(Path.Combine(Root, "schemas", "envelope.schema.json"));
        RawStoredSchema = JsonSchema.FromFile(Path.Combine(Root, "schemas", "events", "raw.stored.schema.json"));
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static string Describe(EvaluationResults results) =>
        string.Join("; ", (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct());

    private static Envelope BridgeEnvelope(string legacyId = "48213", string? url = "https://t.me/monitoringwar/48213")
    {
        var at = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
        var envelope = RawStoredEnvelope.Create(
            "raw-writer@collector-telegram", Guid.CreateVersion7(), "2026.09.15+6550555", "live", 4711, 3, "tg_monitoringwar", legacyId,
            at.AddSeconds(-20), at, at.AddMilliseconds(500), new string('a', 64), isNew: true, "Шахеди на Сумщині", hasPayload: true, url);
        // What OutboxWriter fills in before the insert.
        envelope.EventId = Guid.CreateVersion7();
        envelope.PublishedAt = at.AddSeconds(1);
        envelope.CausationId = envelope.EventId;
        envelope.TopologyVersion = 2;
        return envelope;
    }

    [Fact]
    public void Bridge_raw_stored_envelope_is_valid_against_contract()
    {
        var envelope = BridgeEnvelope();
        var json = JsonNode.Parse(envelope.ToJson())!;
        var result = EnvelopeSchema.Evaluate(ToElement(json), Options);
        Assert.True(result.IsValid, Describe(result));
        var payload = RawStoredSchema.Evaluate(ToElement(json["payload"]!), Options);
        Assert.True(payload.IsValid, Describe(payload));

        Assert.Equal("raw.stored", json["event_type"]!.GetValue<string>());
        Assert.Equal("48213", json["source_message_key"]!.GetValue<string>());
        Assert.Equal("0", json["source_revision"]!.GetValue<string>());
        Assert.Equal(json["event_id"]!.GetValue<string>(), json["causation_id"]!.GetValue<string>()); // bridge root (ADR-0003)
        Assert.Equal(4711, json["payload"]!["raw_message_id"]!.GetValue<int>());
        Assert.Equal(17, json["payload"]!["text_length"]!.GetValue<int>());
    }

    [Fact]
    public void Bridge_envelope_without_url_and_text_is_still_valid()
    {
        var at = DateTimeOffset.UtcNow;
        var envelope = RawStoredEnvelope.Create("raw-writer", Guid.CreateVersion7(), "test", "history", 1, 1, "alerts_in_ua", "31:end",
            at, at, at, new string('b', 64), true, null, true, null);
        envelope.EventId = Guid.CreateVersion7();
        envelope.PublishedAt = at;
        envelope.CausationId = envelope.EventId;
        envelope.TopologyVersion = 2;
        var json = JsonNode.Parse(envelope.ToJson())!;
        Assert.True(EnvelopeSchema.Evaluate(ToElement(json), Options).IsValid, Describe(EnvelopeSchema.Evaluate(ToElement(json), Options)));
        Assert.True(RawStoredSchema.Evaluate(ToElement(json["payload"]!), Options).IsValid);
        Assert.False(json["payload"]!["has_text"]!.GetValue<bool>());
        Assert.Null(json["payload"]!["url"]);
        Assert.Equal("31:end", json["source_message_key"]!.GetValue<string>());
    }

    [Fact]
    public void Identity_cases_fixture_maps_legacy_ids()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "identity-cases.json")))!["cases"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var identity = SourceIdentity.FromLegacy(c!["legacy_source_message_id"]!.GetValue<string>());
            Assert.Equal(c["source_message_key"]!.GetValue<string>(), identity.SourceMessageKey);
            Assert.Equal(c["source_revision"]!.GetValue<string>(), identity.SourceRevision);
        }
        // The original and its edit share the correlation id; a different post does not.
        var original = SourceIdentity.CorrelationId(3, SourceIdentity.FromLegacy("48213").SourceMessageKey);
        var edit = SourceIdentity.CorrelationId(3, SourceIdentity.FromLegacy("48213:e1789466500").SourceMessageKey);
        var other = SourceIdentity.CorrelationId(3, SourceIdentity.FromLegacy("48214").SourceMessageKey);
        Assert.Equal(original, edit);
        Assert.NotEqual(original, other);
        Assert.NotEqual(original, SourceIdentity.CorrelationId(4, "48213"));
        Assert.Equal(5, (original.ToString("N")[12] - '0')); // RFC 4122 version nibble
    }

    [Fact]
    public void Parsing_keeps_unknown_fields_for_the_archive_and_rejects_garbage()
    {
        var fixture = File.ReadAllText(Path.Combine(Root, "fixtures", "valid", "raw.stored.json"));
        var node = JsonNode.Parse(fixture)!.AsObject();
        node["x_future_field"] = "kept";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        var envelope = Envelope.TryParse(bytes, out var error);
        Assert.NotNull(envelope);
        Assert.Null(error);
        Assert.Equal("raw.stored", envelope.EventType);
        Assert.Equal(4711, envelope.RawMessageId);
        Assert.Contains("x_future_field", envelope.ToArchiveJson());
        Assert.DoesNotContain("x_future_field", envelope.ToJson());

        Assert.Null(Envelope.TryParse("not json"u8, out error));
        Assert.NotNull(error);
        Assert.Null(Envelope.TryParse("{\"event_type\":\"raw.stored\"}"u8, out error)); // required envelope members missing
        Assert.NotNull(error);
        var noId = JsonNode.Parse(fixture)!.AsObject();
        noId.Remove("event_id");
        Assert.Null(Envelope.TryParse(JsonSerializer.SerializeToUtf8Bytes(noId), out error));
        Assert.Contains("event_id", error);
    }

    [Fact]
    public void Relay_backoff_is_bounded_with_jitter()
    {
        var relay = new MessagingOptions.RelayOptions { MinBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(30) };
        for (var attempts = 1; attempts <= 12; attempts++)
        {
            var delay = OutboxRelay.Backoff(relay, attempts);
            var expected = Math.Min(Math.Pow(2, attempts - 1), 30);
            Assert.InRange(delay.TotalSeconds, expected, expected * 1.25 + 0.001);
        }
        Assert.InRange(OutboxRelay.Backoff(relay, 100).TotalSeconds, 30, 37.5);
    }
}
