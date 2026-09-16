using Puluj.Infrastructure.Messaging.Topology;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>Registry read from the embedded topology.json: names, expected set by status/lane/version (ADR-0002), schema majors.</summary>
public sealed class TopologyRegistryTests
{
    private static readonly TopologyRegistry Registry = TopologyRegistry.LoadEmbedded();

    [Fact]
    public void Embedded_copy_equals_contract_file()
    {
        var file = TopologyRegistry.Load(Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "topology.json"));
        Assert.Equal(file.Hash, Registry.Hash);
        Assert.Equal(8, Registry.TopologyVersion);
        Assert.Equal("puluj.events", Registry.ExchangeName);
        Assert.Equal(["live", "history", "replay"], Registry.Lanes);
    }

    [Fact]
    public void Names_follow_the_patterns()
    {
        Assert.Equal("puluj.archive.live", Registry.QueueName("archive", "live"));
        Assert.Equal("puluj.archive.live.dlq", Registry.DlqName("archive", "live"));
        Assert.Equal("puluj.history.raw.stored", Registry.RoutingKey("history", "raw.stored"));
        var args = Registry.QueueArguments(Registry.Subscription("archive"), "replay");
        Assert.Equal("quorum", args["x-queue-type"]);
        Assert.Equal(4, args["x-delivery-limit"]);
        Assert.Equal("puluj.dlx", args["x-dead-letter-exchange"]);
        Assert.Equal("puluj.archive.replay.dlq", args["x-dead-letter-routing-key"]);
    }

    [Fact]
    public void Expected_set_is_required_active_or_paused_and_serving_the_lane()
    {
        // topology.json v4: archive, raw-writer, normalizer, parser active; message-analytics still planned → normalizer + archive are expected.
        var expected = Registry.ExpectedSubscriptions("raw.stored", "live");
        Assert.Equal(["normalizer", "archive"], expected.Select(s => s.Id));
        Assert.Equal(["parser"], Registry.ExpectedSubscriptions("message.normalized", "live").Select(s => s.Id));
        Assert.Equal(["finalizer"], Registry.ExpectedSubscriptions("parse.completed", "live").Select(s => s.Id)); // active since v5 (P06)
        Assert.Equal(["archive"], Registry.ExpectedSubscriptions("observations.recorded", "live").Select(s => s.Id)); // domain workers are conditional (by_manifest)
        Assert.Equal(["archive", "track-worker", "alert-worker", "incident-worker"], Registry.ExpectedSubscriptions("observations.recorded", "live", null, null, ["track-worker", "alert-worker", "incident-worker", "nope"]).Select(s => s.Id)); // v7 (P10): every named branch is active
        Assert.Equal(["archive"], Registry.ExpectedSubscriptions("observations.recorded", "replay", null, null, ["track-worker"]).Select(s => s.Id)); // writers do not serve replay (P14)
        Assert.Equal(["archive", "projection"], Registry.ExpectedSubscriptions("track.changed", "live").Select(s => s.Id)); // v8 (P11): projection active; archive stays required
        Assert.Equal(["archive", "projection"], Registry.ExpectedSubscriptions("incident.changed", "live").Select(s => s.Id));
        Assert.Equal(["track-worker"], Registry.ExpectedSubscriptions("track.expiry.requested", "live").Select(s => s.Id));
        Assert.Equal(["archive"], Registry.ExpectedSubscriptions("message.analysis.completed", "live").Select(s => s.Id));
        Assert.Equal(["llm-worker"], Registry.ExpectedSubscriptions("llm.requested", "live").Select(s => s.Id));
        Assert.Equal(["raw-writer", "archive"], Registry.ExpectedSubscriptions("ingress.received", "history").Select(s => s.Id));

        // Database-owned status wins over the file: a paused required consumer stays interested, a planned one activated later joins.
        var overrides = new Dictionary<string, string> { ["archive"] = "paused", ["normalizer"] = "retired", ["message-analytics"] = "active" };
        Assert.Equal(["message-analytics", "archive"], Registry.ExpectedSubscriptions("raw.stored", "live", overrides).Select(s => s.Id));

        // Lanes: projection has no replay lane, so it is never expected for a replay-lane track.changed (a shadow generation never reaches the live map).
        Assert.DoesNotContain("projection", Registry.ExpectedSubscriptions("track.changed", "replay").Select(s => s.Id));
        Assert.DoesNotContain("projection", Registry.ExpectedSubscriptions("incident.changed", "replay").Select(s => s.Id));

        // Retired/planned are not expected.
        Assert.Empty(Registry.ExpectedSubscriptions("raw.stored", "live", new Dictionary<string, string> { ["archive"] = "retired", ["normalizer"] = "retired" }));
        Assert.Throws<KeyNotFoundException>(() => Registry.ExpectedSubscriptions("no.such.event", "live"));
    }

    [Fact]
    public void Every_replay_source_event_expects_archive()
    {
        foreach (var definition in Registry.Events.Values.Where(e => e.ReplaySource))
        {
            Assert.Contains("archive", definition.RequiredSubscriptions);
        }
        Assert.True(Registry.Event("raw.stored").ReplaySource);
        Assert.Equal(1, Registry.Event("raw.stored").SchemaMajor);
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("1.3", 1)]
    [InlineData("2.0", 2)]
    [InlineData("0.9", 0)]
    [InlineData("v1", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData("1.", null)]
    public void Schema_major_parsing_matches_compatibility_rule(string version, int? major)
    {
        Assert.Equal(major, TopologyRegistry.ParseMajor(version));
    }
}
