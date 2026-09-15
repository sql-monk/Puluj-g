using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing.Indexes;
using Puluj.Processing.Tests.Support;

namespace Puluj.Processing.Tests.Seeding;

/// <summary>P07 / plan §8.2: legacy mapping, seed file, contract parity and the per-message catalog snapshot.</summary>
public sealed class EventKindCatalogTests
{
    private static EventKindSeeder.EventKindsFile SeedFile()
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data", "taxonomy", "event-kinds.json"));
        return JsonSerializer.Deserialize<EventKindSeeder.EventKindsFile>(stream, SeedFiles.Json)!;
    }

    private static JsonObject CommonSchema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestIndexes.RepoRoot, "contracts", "messaging", "schemas", "common.schema.json")))!.AsObject();

    /// <summary>The §8.2 initial vocabulary plus the explicit unknown outcome; casualties.reported is deliberately absent.</summary>
    private static readonly string[] ExpectedCodes =
    [
        "target.observed", "target.launch", "target.cancelled",
        "alert.air_raid.started", "alert.air_raid.ended",
        "air_defence.activity", "air_defence.interception.reported",
        "impact.explosion.reported", "impact.confirmed",
        "fire.reported", "damage.reported", "infrastructure.outage",
        "civil_defence.notice", "evacuation.notice",
        "unknown.unclassified",
    ];

    [Fact]
    public void Legacy_map_covers_every_enum_member_with_unique_codes_and_round_trips()
    {
        var members = Enum.GetValues<EventType>();
        var codes = members.Select(EventKindLegacyMap.ToCode).ToList();
        Assert.Equal(members.Length, codes.Distinct(StringComparer.Ordinal).Count());
        foreach (var m in members)
        {
            Assert.Equal(m, EventKindLegacyMap.ToEventType(EventKindLegacyMap.ToCode(m)));
            Assert.True(EventKindCodes.IsValid(EventKindLegacyMap.ToCode(m)));
        }
        Assert.Equal("unknown.unclassified", EventKindLegacyMap.ToCode(EventType.Unknown));
        Assert.Equal("alert.air_raid.ended", EventKindLegacyMap.ToCode(EventType.AlertCancelled));
        // A kind without an enum member falls back to Unknown for the legacy column — documented, not invented.
        Assert.Null(EventKindLegacyMap.ToEventType("fire.reported"));
        Assert.Equal(EventType.Unknown, EventKindLegacyMap.LegacyOrFallback("fire.reported"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EventKindLegacyMap.ToCode((EventType)999));
    }

    [Fact]
    public void Seed_file_matches_plan_vocabulary_and_passes_seeder_validation()
    {
        var file = SeedFile();
        EventKindSeeder.Validate(file);
        Assert.Equal(1, file.PolicyVersion);
        Assert.Equal(ExpectedCodes.Order(StringComparer.Ordinal), file.Kinds.Select(k => k.Code).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(file.Kinds, k => k.Code == "casualties.reported");
        // Every legacy member is represented in the seed metadata exactly once, and only on its mapped code.
        var legacy = file.Kinds.Where(k => k.LegacyEventType is not null).ToDictionary(k => k.LegacyEventType!.Value, k => k.Code);
        Assert.Equal(Enum.GetValues<EventType>().Order(), legacy.Keys.Order());
        foreach (var (type, code) in legacy)
        {
            Assert.Equal(EventKindLegacyMap.ToCode(type), code);
        }
        var unknown = file.Kinds.Single(k => k.Code == "unknown.unclassified");
        Assert.Equal("info", unknown.Category);
        Assert.False(unknown.MapVisible ?? true);
        Assert.All(file.Kinds, k => Assert.False(string.IsNullOrWhiteSpace(k.NameUk)));
    }

    [Fact]
    public void Seed_codes_and_categories_match_the_messaging_contract()
    {
        var defs = CommonSchema()["$defs"]!;
        var pattern = new Regex(defs["eventKindCode"]!["pattern"]!.GetValue<string>());
        var categories = defs["observationCategory"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["alert", "incident", "info", "target"], categories.Order(StringComparer.Ordinal));
        // The C# enum is the contract list, stored lowercase.
        Assert.Equal(categories.Order(StringComparer.Ordinal), Enum.GetNames<EventKindCategory>().Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal));
        foreach (var k in SeedFile().Kinds)
        {
            Assert.Matches(pattern, k.Code);
            Assert.Contains(k.Category, categories);
        }
        // Codes the P01 fixtures already use exist in the seed with the category the fixtures assume.
        var byCode = SeedFile().Kinds.ToDictionary(k => k.Code);
        Assert.Equal("target", byCode["target.observed"].Category);
        Assert.Equal("incident", byCode["air_defence.activity"].Category);
        Assert.Equal("alert", byCode["alert.air_raid.started"].Category);
    }

    [Fact]
    public void Seeder_validation_rejects_bad_code_duplicate_category_and_legacy_drift()
    {
        static EventKindSeeder.EventKindSeedEntry Kind(string code, string category = "info", JsonElement? metadata = null) =>
            new(code, code, category, null, null, null, null, null, null, null, null, null, null, null, null, null, metadata);
        var good = SeedFile().Kinds;

        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("BadCode")])));
        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("fire.reported")])));
        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("x.y", "weapon")])));
        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("x.y", "2")]))); // numeric enum string is not a category name
        var badLegacy = JsonDocument.Parse("""{"legacyEventType":"NoSuchMember"}""").RootElement;
        var ex = Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("x.y", "info", badLegacy)])));
        Assert.Contains("legacyEventType", ex.Message);
        var drift = JsonDocument.Parse("""{"legacyEventType":"ExplosionReport"}""").RootElement;
        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, [.. good, Kind("x.y", "info", drift)])));
        // Dropping a legacy-mapped code from the seed is an error: the map would point at nothing.
        Assert.Throws<InvalidOperationException>(() => EventKindSeeder.Validate(new(1, good.Where(k => k.Code != "target.observed").ToList())));
    }

    private static EventKind Row(int id, string code, int policy = 1) => new() { EventKindId = id, Code = code, NameUk = code, PolicyVersion = policy };

    [Fact]
    public void Index_stamps_mapped_kinds_leaves_unmapped_null_and_never_overwrites()
    {
        var index = new EventKindIndex([Row(1, "target.observed"), Row(2, "alert.air_raid.started", 3)]);
        Assert.Equal(2, index.Count);
        Assert.Equal(3, index.PolicyVersion);
        Assert.Equal(1, index.IdForLegacy(EventType.TargetObserved));
        Assert.Null(index.IdForLegacy(EventType.ExplosionReport)); // not seeded → no guess
        var targets = new List<Target>
        {
            new() { EventType = EventType.TargetObserved },
            new() { EventType = EventType.ExplosionReport },
            new() { EventType = EventType.AirRaidAlert, EventKindId = 42 }, // already stamped by another snapshot
        };
        Assert.Equal(1, index.Stamp(targets));
        Assert.Equal(1, targets[0].EventKindId);
        Assert.Null(targets[1].EventKindId);
        Assert.Equal(42, targets[2].EventKindId);
        Assert.Equal(EventType.TargetObserved, targets[0].EventType); // legacy enum untouched (§8.2 compatibility)
        Assert.Equal(3, targets[0].ParserMetadata!.RootElement.GetProperty("eventKindPolicyVersion").GetInt32());
        Assert.Null(targets[1].ParserMetadata); // nothing stamped, nothing cited
        // Existing parser metadata is preserved, the policy version is added next to it.
        var withMetadata = new Target { EventType = EventType.TargetObserved, ParserMetadata = JsonDocument.Parse("{\"rules\":[\"r1\"]}") };
        index.Stamp([withMetadata]);
        Assert.Equal("r1", withMetadata.ParserMetadata!.RootElement.GetProperty("rules")[0].GetString());
        Assert.Equal(3, withMetadata.ParserMetadata.RootElement.GetProperty("eventKindPolicyVersion").GetInt32());
        Assert.True(EventKindIndex.Empty.IsEmpty);
        Assert.Equal(0, EventKindIndex.Empty.Stamp(targets));
    }

    [Fact]
    public void Snapshot_is_pinned_per_message_a_refresh_does_not_change_it()
    {
        var before = new EventKindIndex([Row(1, "target.observed")]);
        var after = new EventKindIndex([Row(7, "target.observed"), Row(8, "impact.explosion.reported")]);
        var target = new Target { EventType = EventType.TargetObserved };
        before.Stamp([target]); // the message started with `before`; `after` was loaded meanwhile
        Assert.Equal(1, target.EventKindId);
        Assert.Equal(7, after.IdForLegacy(EventType.TargetObserved));
    }

    [Fact]
    public void Backfill_sql_mirrors_the_legacy_map()
    {
        foreach (var (type, code) in EventKindLegacyMap.All)
        {
            Assert.Contains($"WHEN {(int)type} THEN '{code}'", EventKindBackfill.UpdateSql);
        }
        Assert.Contains("event_kind_id IS NULL", EventKindBackfill.UpdateSql);
        Assert.Contains("BETWEEN @from AND @to", EventKindBackfill.UpdateSql);
        // The manual script carries the same mapping.
        var sql = File.ReadAllText(Path.Combine(TestIndexes.RepoRoot, "scripts", "backfill-event-kinds.sql"));
        foreach (var (type, code) in EventKindLegacyMap.All)
        {
            Assert.Contains($"WHEN {(int)type} THEN '{code}'", sql);
        }
        Assert.Contains("pg_advisory_xact_lock(88327283100161)", sql);
    }
}
