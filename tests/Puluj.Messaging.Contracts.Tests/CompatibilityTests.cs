using System.Text.Json.Nodes;

namespace Puluj.Messaging.Contracts.Tests;

/// <summary>T10, T15: правило сумісності schema_version (ADR-0003), матриця compatibility.json, analytics out-of-order.</summary>
[Collection(ContractCollection.Name)]
public sealed class CompatibilityTests(ContractFiles contracts)
{
    /// <summary>Еталонна реалізація правила з compatibility.json: той самий MAJOR → compatible; інакше (включно з
    /// невалідною версією або невідомим типом) → quarantine. Runtime-реалізація P03 має повторити саме це.</summary>
    private static string Decide(IReadOnlyDictionary<string, string> supported, string eventType, string eventVersion)
    {
        if (!supported.TryGetValue(eventType, out var consumerVersion))
        {
            return "quarantine";
        }
        return TryMajor(eventVersion, out var eventMajor) && TryMajor(consumerVersion, out var consumerMajor) && eventMajor == consumerMajor
            ? "compatible"
            : "quarantine";

        static bool TryMajor(string version, out int major)
        {
            major = 0;
            var parts = version.Split('.');
            return parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out _) && major >= 0;
        }
    }

    [Fact]
    public void T10_MatrixCasesMatchTheMajorEqualRule()
    {
        var matrix = contracts.LoadObject(Path.Combine("fixtures", "compatibility.json"));
        Assert.Equal("major_equal", matrix["rule"]!.GetValue<string>());
        var supported = matrix["consumer_supported"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
        var cases = matrix["cases"]!.AsArray();
        Assert.True(cases.Count >= 6);
        var decisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            var expected = c!["decision"]!.GetValue<string>();
            decisions.Add(expected);
            Assert.Equal(expected, Decide(supported, c["event_type"]!.GetValue<string>(), c["event_schema_version"]!.GetValue<string>()));
            if (c["fixture"] is { } fixtureRef)
            {
                // Case, що посилається на fixture, має збігатися з ним за event_type і schema_version (review B3).
                var fixture = contracts.LoadObject(Path.Combine("fixtures", fixtureRef.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)));
                Assert.Equal(c["event_type"]!.GetValue<string>(), fixture["event_type"]!.GetValue<string>());
                Assert.Equal(c["event_schema_version"]!.GetValue<string>(), fixture["schema_version"]!.GetValue<string>());
            }
        }
        Assert.Equal(["compatible", "quarantine"], decisions.Order(StringComparer.Ordinal));
        // Невідома/невалідна версія — рішення, не exception.
        Assert.Equal("quarantine", Decide(supported, "raw.stored", "v1"));
        Assert.Equal("quarantine", Decide(supported, "raw.stored", ""));
        Assert.Equal("quarantine", Decide(supported, "unknown.event", "1.0"));
    }

    [Fact]
    public void T10_AdditiveMinorChange_PassesCurrentSchema()
    {
        var additive = contracts.LoadObject(Path.Combine("fixtures", "valid", "raw.stored.additive-1.1.json"));
        Assert.Equal("1.1", additive["schema_version"]!.GetValue<string>());
        Assert.NotNull(additive["x_transport_hint"]);            // додаткове envelope поле
        Assert.NotNull(additive["payload"]!["media_count"]);      // додаткове payload поле
        var (ok, errors) = contracts.Validate(additive);
        Assert.True(ok, errors);

        // Схеми не забороняють невідомі поля на верхньому рівні envelope/payload — інакше additive зміни ламали б consumers.
        Assert.Null(contracts.LoadObject(Path.Combine("schemas", "envelope.schema.json"))["additionalProperties"]);
        foreach (var (eventType, definition) in contracts.Events)
        {
            var schema = contracts.LoadObject(definition!["schema"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            Assert.True(schema["additionalProperties"] is null, $"{eventType}: top-level additionalProperties forbids additive changes");
        }
    }

    [Fact]
    public void T10_BreakingChanges_FailCurrentSchema()
    {
        var baseline = contracts.LoadObject(Path.Combine("fixtures", "valid", "raw.stored.json"));
        Assert.True(contracts.Validate(baseline).IsValid);

        var removedRequired = baseline.DeepClone().AsObject();
        removedRequired["payload"]!.AsObject().Remove("content_hash");
        var (ok1, err1) = contracts.Validate(removedRequired);
        Assert.False(ok1);
        Assert.Contains("content_hash", err1);

        var typeChanged = baseline.DeepClone().AsObject();
        typeChanged["payload"]!["raw_message_id"] = "4711";
        var (ok2, err2) = contracts.Validate(typeChanged);
        Assert.False(ok2);
        Assert.Contains("/payload/raw_message_id", err2);

        // Матриця документує саме ці два приклади.
        var examples = contracts.LoadObject(Path.Combine("fixtures", "compatibility.json"))["breaking_examples"]!.AsArray();
        Assert.Equal(2, examples.Count);
    }

    [Fact]
    public void T15_AnalyticsOutOfOrderSequence_ReferencesValidFixturesAndPartialUpsert()
    {
        var seq = contracts.LoadObject(Path.Combine("fixtures", "sequences", "analytics-out-of-order.json"));
        var order = ContractFiles.Strings(seq["order"]).ToList();
        Assert.Equal(["message.analysis.completed.json", "raw.stored.json"], order);
        long? rawId = null;
        foreach (var file in order)
        {
            var envelope = contracts.LoadObject(Path.Combine("fixtures", "valid", file));
            Assert.True(contracts.Validate(envelope).IsValid, file);
            var id = envelope["raw_message_id"]!.GetValue<long>();
            Assert.True(rawId is null || rawId == id, "sequence must describe one raw message");
            rawId = id;
        }
        var expect = seq["expect"]!;
        var afterFirst = expect["after_first"]!["analytics.message_lifecycle"]!;
        var afterSecond = expect["after_second"]!["analytics.message_lifecycle"]!;
        Assert.Equal(rawId, afterFirst["raw_message_id"]!.GetValue<long>());
        Assert.Null(afterFirst["stored_at"]);      // completion прийшла першою: частковий запис без raw metadata
        Assert.NotNull(afterFirst["analyzed_at"]);
        Assert.NotNull(afterSecond["stored_at"]);  // reconciliation дозаповнила, не перезаписала
        Assert.Equal(afterFirst["analyzed_at"]!.GetValue<string>(), afterSecond["analyzed_at"]!.GetValue<string>());
        Assert.Equal(1, expect["root_message_count_increment"]!.GetValue<int>());
    }
}
