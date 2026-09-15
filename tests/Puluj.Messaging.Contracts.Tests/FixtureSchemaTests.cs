using System.Text.Json.Nodes;

namespace Puluj.Messaging.Contracts.Tests;

/// <summary>T01, T02, T11, T12, T13: fixtures проти envelope/payload схем (plan P01, ADR-0003/0004).</summary>
[Collection(ContractCollection.Name)]
public sealed class FixtureSchemaTests(ContractFiles contracts)
{
    public static TheoryData<string> ValidFixtures()
    {
        var data = new TheoryData<string>();
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "fixtures", "valid");
        foreach (var file in Directory.EnumerateFiles(root, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    public static TheoryData<string> InvalidFixtures()
    {
        var data = new TheoryData<string>();
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "fixtures", "invalid");
        foreach (var file in Directory.EnumerateFiles(root, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void T01_ValidFixture_PassesEnvelopeAndPayloadSchema(string file)
    {
        var envelope = contracts.LoadObject(Path.Combine("fixtures", "valid", file));
        var (ok, errors) = contracts.Validate(envelope);
        Assert.True(ok, $"{file}: {errors}");
    }

    [Fact]
    public void T01_EveryEventTypeHasAValidFixture()
    {
        var covered = contracts.Files(Path.Combine("fixtures", "valid"))
            .Select(f => contracts.LoadObject(Path.Combine("fixtures", "valid", Path.GetFileName(f)))["event_type"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var missing = contracts.Events.Select(e => e.Key).Where(t => !covered.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "event types without a valid fixture: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void T02_InvalidFixture_FailsWithExpectedPath(string file)
    {
        var wrapper = contracts.LoadObject(Path.Combine("fixtures", "invalid", file));
        var reason = wrapper["reason"]!.GetValue<string>();
        var expectedPath = wrapper["expected_error_contains"]!.GetValue<string>();
        var (ok, errors) = contracts.Validate(wrapper["event"]!);
        Assert.False(ok, $"{file} ({reason}) unexpectedly valid");
        if (expectedPath.Length > 0)
        {
            Assert.True(errors.Contains(expectedPath, StringComparison.Ordinal),
                $"{file} ({reason}): expected an error at {expectedPath}, got: {errors}");
        }
    }

    [Fact]
    public void T11_ConditionalRequiredFields_PerEventScope()
    {
        // ingress.received: без raw_message_id і з causation_id = null; решта message-scoped — з raw_message_id.
        var ingress = contracts.LoadObject(Path.Combine("fixtures", "valid", "ingress.received.json"));
        Assert.Null(ingress["raw_message_id"]);
        Assert.Null(ingress["causation_id"]);

        foreach (var (eventType, definition) in contracts.Events)
        {
            var fixture = contracts.LoadObject(Path.Combine("fixtures", "valid", eventType + ".json"));
            switch (definition!["scope"]!.GetValue<string>())
            {
                case "message" when eventType != "ingress.received":
                    Assert.True(fixture["raw_message_id"] is not null, $"{eventType}: raw_message_id required");
                    Assert.True(fixture["causation_id"] is not null, $"{eventType}: causation_id required");
                    break;
                case "aggregate":
                    Assert.True(fixture["aggregate_id"] is not null, $"{eventType}: aggregate_id required");
                    Assert.True(fixture["aggregate_revision"] is not null, $"{eventType}: aggregate_revision required");
                    break;
            }
        }
    }

    [Fact]
    public void T12_RepublishSameEvent_EnvelopeIdentical()
    {
        var seq = contracts.LoadObject(Path.Combine("fixtures", "sequences", "republish-same-event.json"));
        var first = seq["first"]!["envelope"]!;
        var again = seq["republish"]!["envelope"]!;
        Assert.True(JsonNode.DeepEquals(first, again), "republished envelope differs from the first publication");
        Assert.NotEqual(seq["first"]!["transport"]!["sent_at"]!.GetValue<string>(), seq["republish"]!["transport"]!["sent_at"]!.GetValue<string>());
        Assert.True(contracts.Validate(first).IsValid);
    }

    [Fact]
    public void T13_IdentityCases_MatchFixturesAndRules()
    {
        var cases = contracts.LoadObject(Path.Combine("fixtures", "identity-cases.json"))["cases"]!.AsArray();
        Assert.Equal(4, cases.Count);
        foreach (var c in cases)
        {
            var fixture = contracts.LoadObject(Path.Combine("fixtures", c!["fixture"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)));
            var payload = fixture["payload"]!;
            var legacy = c["legacy_source_message_id"]!.GetValue<string>();
            var key = c["source_message_key"]!.GetValue<string>();
            var revision = c["source_revision"]!.GetValue<string>();

            Assert.Equal(key, fixture["source_message_key"]!.GetValue<string>());
            Assert.Equal(revision, fixture["source_revision"]!.GetValue<string>());
            Assert.Equal(key, payload["source_message_key"]!.GetValue<string>());
            Assert.Equal(revision, payload["source_revision"]!.GetValue<string>());
            Assert.Equal(legacy, payload["legacy_source_message_id"]!.GetValue<string>());
            Assert.Equal(c["source_code"]!.GetValue<string>(), payload["source_code"]!.GetValue<string>());

            // Правило ADR-0003: Telegram edit → той самий key, revision "e{unix}"; alerts phase → частина key, revision "0".
            if (System.Text.RegularExpressions.Regex.IsMatch(legacy, "^[0-9]+:e[0-9]+$"))
            {
                Assert.Equal(legacy[..legacy.IndexOf(':')], key);
                Assert.StartsWith("e", revision, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(legacy, key);
                Assert.Equal("0", revision);
            }
        }

        // Редакція і оригінал — окремі події з окремими correlation? Ні: той самий пост → та сама correlation_id (§5.1).
        var original = contracts.LoadObject(Path.Combine("fixtures", "valid", "ingress.received.json"));
        var edit = contracts.LoadObject(Path.Combine("fixtures", "valid", "ingress.received.telegram-edit.json"));
        Assert.Equal(original["correlation_id"]!.GetValue<string>(), edit["correlation_id"]!.GetValue<string>());
        Assert.NotEqual(original["event_id"]!.GetValue<string>(), edit["event_id"]!.GetValue<string>());

        // Start і end тривоги — різні джерельні повідомлення: різні correlation_id.
        var start = contracts.LoadObject(Path.Combine("fixtures", "valid", "ingress.received.alerts-start.json"));
        var end = contracts.LoadObject(Path.Combine("fixtures", "valid", "ingress.received.alerts-end.json"));
        Assert.NotEqual(start["correlation_id"]!.GetValue<string>(), end["correlation_id"]!.GetValue<string>());
        // Payload — форма Wrap() collector: {kind, at, alert}; для :end business time = час спостереження (ADR-0003).
        foreach (var fixture in new[] { start, end })
        {
            var rawPayload = fixture["payload"]!["raw_payload"]!;
            Assert.NotNull(rawPayload["kind"]);
            Assert.NotNull(rawPayload["at"]);
            Assert.NotNull(rawPayload["alert"]);
        }
        Assert.Equal("alert.finished", end["payload"]!["raw_payload"]!["kind"]!.GetValue<string>());
        Assert.Equal(end["payload"]!["raw_payload"]!["at"]!.GetValue<string>(), end["payload"]!["source_published_at"]!.GetValue<string>());
        Assert.Equal(end["payload"]!["source_published_at"]!.GetValue<string>(), end["payload"]!["received_at"]!.GetValue<string>());
    }
}
