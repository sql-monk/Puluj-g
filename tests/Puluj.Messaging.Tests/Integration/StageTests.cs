using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Puluj.Domain;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging.Tests.Unit;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P05 stage workers (normalizer, rules/structured parser) on the real pipeline: ingress → raw-writer → normalizer →
/// parser. Every assert is on committed rows (`processing.stage_results`, outbox events, receipts) and — for the
/// acceptance criterion — on what the stages did NOT write: `targets` and `air_alerts` stay empty.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class StageTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly JsonSchema NormalizedSchema = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "message.normalized.schema.json"));
    private static readonly JsonSchema ParseSchema = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "parse.completed.schema.json"));
    private static readonly JsonSchema LlmSchema = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "llm.requested.schema.json"));

    private async Task StartAllAsync()
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Parser.StartAsync(None);
    }

    private async Task StopAllAsync()
    {
        await f.Parser.StopAsync(None);
        await f.Normalizer.StopAsync(None);
        await f.Archive.StopAsync(None);
        await f.RawWriter.StopAsync(None);
        await f.Relay.StopAsync(None);
    }

    /// <summary>Publishes through the collectors' ingress, runs every stage and waits for <paramref name="parsed"/> parser receipts.</summary>
    private async Task RunAsync(int parsed, params (string Id, string? Text, JsonDocument? Payload)[] messages)
    {
        var source = await f.SourceAsync();
        foreach (var (id, text, payload) in messages)
        {
            var msg = f.Message(id, text) with { RawPayload = payload };
            await f.Ingress.PublishAsync(msg, source, "test", null, live: true, None);
        }
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'parser' AND outcome IS NOT NULL") == parsed, TimeSpan.FromSeconds(40)),
                $"parser receipts: {await f.CountAsync("processing.deliveries", "subscription_id = 'parser' AND outcome IS NOT NULL")} of {parsed}");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0)); // the relay published the parser's events
        }
        finally
        {
            await StopAllAsync();
        }
    }

    private async Task<List<JsonNode>> EventsAsync(string eventType)
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var rows = await db.Outbox.AsNoTracking().Where(o => o.EventType == eventType).OrderBy(o => o.OutboxId).ToListAsync();
        return rows.Select(o => JsonNode.Parse(o.Envelope.RootElement.GetRawText())!).ToList();
    }

    private static void Valid(JsonSchema schema, JsonNode payload)
    {
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(payload), ContractSchemas.Options);
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct()));
    }

    private static void ValidEnvelope(JsonNode envelope)
    {
        var result = ContractSchemas.Envelope.Evaluate(JsonSerializer.SerializeToElement(envelope), ContractSchemas.Options);
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Distinct()));
    }

    [Fact]
    public async Task S01_Text_with_a_target_goes_through_both_stages_without_domain_writes()
    {
        await f.ResetAsync();
        await RunAsync(1, ("s01", "Шахеди на Сумщині курсом на Полтавщину.", null));

        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'normalize' AND outcome = 'completed' AND outputs->>'text_kind' = 'text'"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'parse' AND outcome = 'facts' AND (outputs->>'facts_count')::int >= 1"));
        Assert.Equal(0, await f.CountAsync("targets"));
        Assert.Equal(0, await f.CountAsync("air_alerts"));
        Assert.Equal(0, await f.CountAsync("raw_messages", "processing_status <> 0")); // the legacy loop is not running: still Pending (0 = Pending)
        Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'normalizer' AND state = 'succeeded' AND stage_result_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'parser' AND state = 'succeeded' AND stage_result_id IS NOT NULL"));

        var normalized = Assert.Single(await EventsAsync("message.normalized"));
        ValidEnvelope(normalized);
        Valid(NormalizedSchema, normalized["payload"]!);
        var stored = Assert.Single(await EventsAsync("raw.stored"));
        Assert.Equal(stored["event_id"]!.GetValue<string>(), normalized["causation_id"]!.GetValue<string>());
        Assert.Equal(stored["correlation_id"]!.GetValue<string>(), normalized["correlation_id"]!.GetValue<string>());
        Assert.Equal("norm-1", normalized["payload"]!["normalization_version"]!.GetValue<string>());
        Assert.Equal("uk", normalized["payload"]!["language"]!.GetValue<string>());

        var parsed = Assert.Single(await EventsAsync("parse.completed"));
        ValidEnvelope(parsed);
        Valid(ParseSchema, parsed["payload"]!);
        Assert.Equal(normalized["event_id"]!.GetValue<string>(), parsed["causation_id"]!.GetValue<string>());
        var payload = parsed["payload"]!;
        Assert.Equal("facts", payload["outcome"]!.GetValue<string>());
        Assert.Equal("rules", payload["method"]!.GetValue<string>());
        var fact = payload["facts"]!.AsArray()[0]!;
        Assert.Equal("target.observed", fact["event_kind_code"]!.GetValue<string>());
        Assert.Equal("target", fact["category"]!.GetValue<string>());
        Assert.NotNull(fact["location"]!["place_id"]);
        Assert.Equal("rule-0.1", fact["evidence"]!["rule_version"]!.GetValue<string>());
        Assert.NotNull(fact["attributes"]!["target_category_id"]);
        Assert.Equal("norm-1", payload["versions"]!["normalization"]!.GetValue<string>());
        Assert.Equal(payload["attempt_id"]!.GetValue<string>(), await f.ScalarAsync<string>("SELECT outputs->>'attempt_id' FROM processing.stage_results WHERE stage = 'parse'"));
        Assert.Empty(await EventsAsync("llm.requested"));
        // parse.completed waits in the paused finalizer queue (P06): expected, not unroutable.
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'finalizer' AND outcome IS NULL"));
        Assert.Equal(0, await f.CountAsync("messaging.outbox", "confirmed_at IS NULL"));
        f.Evidence.Record("P05-S01", new { stages = new { normalize = "completed", parse = "facts" }, facts = payload["facts"]!.AsArray().Count, targets = 0, air_alerts = 0, unconfirmed_outbox = 0, finalizer_backlog = 1 });
    }

    [Fact]
    public async Task S02_Structured_alerts_give_pure_alert_facts_and_no_air_alert_rows()
    {
        await f.ResetAsync();
        var start = JsonDocument.Parse("{\"kind\":\"alert.started\",\"at\":\"2026-09-15T09:58:00Z\",\"alert\":{\"id\":31,\"location_title\":\"Київська область\",\"location_oblast\":\"Київська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"started_at\":\"2026-09-15T09:58:00Z\"}}");
        var end = JsonDocument.Parse("{\"kind\":\"alert.finished\",\"at\":\"2026-09-15T10:41:00Z\",\"alert\":{\"id\":31,\"location_title\":\"Київська область\",\"location_oblast\":\"Київська область\",\"location_type\":\"oblast\",\"alert_type\":\"air_raid\",\"started_at\":\"2026-09-15T09:58:00Z\",\"finished_at\":\"2026-09-15T10:41:00Z\"}}");
        await RunAsync(2, ("31:start", null, start), ("31:end", null, end));

        Assert.Equal(0, await f.CountAsync("air_alerts"));
        Assert.Equal(0, await f.CountAsync("targets"));
        Assert.Equal(2, await f.CountAsync("processing.stage_results", "stage = 'normalize' AND outputs->>'text_kind' = 'structured'"));
        Assert.Equal(2, await f.CountAsync("processing.stage_results", "stage = 'parse' AND outcome = 'facts' AND stage_version = 'alerts_in_ua-adapter-1'"));
        var normalized = await EventsAsync("message.normalized");
        Assert.Equal(["alerts_in_ua.alert.finished", "alerts_in_ua.alert.started"], normalized.Select(n => n["payload"]!["structured_kind"]!.GetValue<string>()).Order());
        var parsed = await EventsAsync("parse.completed");
        Assert.Equal(2, parsed.Count);
        var codes = new List<string>();
        foreach (var p in parsed)
        {
            Valid(ParseSchema, p["payload"]!);
            Assert.Equal("structured", p["payload"]!["method"]!.GetValue<string>());
            var fact = Assert.Single(p["payload"]!["facts"]!.AsArray());
            codes.Add(fact!["event_kind_code"]!.GetValue<string>());
            Assert.Equal("alert", fact["category"]!.GetValue<string>());
            Assert.Equal("confirmed", fact["confidence"]!.GetValue<string>());
            Assert.Equal("region", fact["location"]!["kind"]!.GetValue<string>());
            Assert.NotNull(fact["location"]!["place_id"]);
            Assert.Equal("alert", fact["evidence"]!["structured_field"]!.GetValue<string>());
            Assert.Equal("31", fact["attributes"]!["parser_metadata"]!["sourceAlertId"]!.GetValue<string>());
            Assert.NotNull(fact["attributes"]!["parser_metadata"]!["startedAt"]);
        }
        Assert.Equal(["alert.air_raid.ended", "alert.air_raid.started"], codes.Order());
        f.Evidence.Record("P05-S02", new { structured = 2, facts = codes.Order(), air_alerts = 0, targets = 0 });
    }

    [Fact]
    public async Task S03_No_text_no_structured_payload_is_unsupported()
    {
        await f.ResetAsync();
        await RunAsync(1, ("s03", null, JsonDocument.Parse("{\"kind\":\"dev.ingest\"}")));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'normalize' AND outputs->>'text_kind' = 'empty'"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'parse' AND outcome = 'unsupported' AND stage_version = 'empty'"));
        var parsed = Assert.Single(await EventsAsync("parse.completed"));
        Valid(ParseSchema, parsed["payload"]!);
        Assert.Equal("unsupported", parsed["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal("none", parsed["payload"]!["method"]!.GetValue<string>());
        Assert.Empty(parsed["payload"]!["facts"]!.AsArray());
        f.Evidence.Record("P05-S03", new { text_kind = "empty", outcome = "unsupported" });
    }

    [Fact]
    public async Task S04_Text_without_facts_is_no_facts_and_visible()
    {
        await f.ResetAsync();
        await RunAsync(1, ("s04", "Доброго ранку, друзі! Гарного дня.", null));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'parse' AND outcome = 'no_facts'"));
        var parsed = Assert.Single(await EventsAsync("parse.completed"));
        Valid(ParseSchema, parsed["payload"]!);
        Assert.Equal("no_facts", parsed["payload"]!["outcome"]!.GetValue<string>());
        Assert.Null(parsed["payload"]!["fallback_reason"]); // not a target report: no LLM question at all
        Assert.Empty(await EventsAsync("llm.requested"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'parser' AND outcome = 'completed'"));
        f.Evidence.Record("P05-S04", new { outcome = "no_facts", llm_requested = 0 });
    }

    [Fact]
    public async Task S05_Multi_fact_message_gives_one_fact_per_segment()
    {
        await f.ResetAsync();
        await RunAsync(1, ("s05", "Шахеди на Сумщині курсом на Полтавщину.\nБпЛА на Полтавщині у південному напрямку.", null));
        var parsed = Assert.Single(await EventsAsync("parse.completed"));
        Valid(ParseSchema, parsed["payload"]!);
        var facts = parsed["payload"]!["facts"]!.AsArray();
        Assert.True(facts.Count >= 2, $"facts: {facts.Count}");
        Assert.Equal(facts.Count, await f.ScalarAsync<int>("SELECT (outputs->>'facts_count')::int FROM processing.stage_results WHERE stage = 'parse'"));
        Assert.True(facts.Select(x => x!["evidence"]!["segment_index"]!.GetValue<int>()).Distinct().Count() >= 2);
        Assert.All(facts, x => Assert.Equal("target.observed", x!["event_kind_code"]!.GetValue<string>()));
        f.Evidence.Record("P05-S05", new { facts = facts.Count, segments = facts.Select(x => x!["evidence"]!["segment_index"]!.GetValue<int>()).Distinct().Count() });
    }

    [Fact]
    public async Task S06_Second_raw_stored_for_the_same_raw_does_not_repeat_the_stage()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        var msg = f.Message("s06", "Шахеди на Сумщині курсом на Полтавщину.");
        await f.Ingress.PublishAsync(msg, source, "test", null, live: true, None);
        await f.Ingress.PublishAsync(msg, source, "test", null, live: true, None); // a collector re-read: raw.stored{is_new:false}
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND outcome IS NOT NULL") == 2, TimeSpan.FromSeconds(40)));
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'parser' AND outcome = 'completed'") == 1, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await StopAllAsync();
        }
        Assert.Equal(2, await EventsAsync("raw.stored").Then(e => e.Count));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND outcome = 'completed'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'normalizer' AND outcome = 'noop'"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'normalize'"));
        Assert.Single(await EventsAsync("message.normalized"));
        Assert.Single(await EventsAsync("parse.completed"));
        f.Evidence.Record("P05-S06", new { raw_stored = 2, normalize_stage_rows = 1, normalized_events = 1, normalizer_noop = 1 });
    }

    [Fact]
    public async Task S07_Target_report_without_rule_match_asks_the_llm_only_for_fresh_live_posts()
    {
        await f.ResetAsync();
        var source = await f.SourceAsync();
        const string text = "Летить щось невідоме, загроза для півдня."; // trigger stems, no recognizable target/place for the rules
        await f.Ingress.PublishAsync(f.Message("s07-live", text), source, "test", null, live: true, None);
        await f.Ingress.PublishAsync(f.Message("s07-stale", text, DateTimeOffset.UtcNow.AddDays(-10)), source, "test", null, live: true, None);
        await f.Ingress.PublishAsync(f.Message("s07-history", text), source, "test", null, live: false, None);
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'parser' AND outcome IS NOT NULL") == 3, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await StopAllAsync();
        }
        var parsed = await EventsAsync("parse.completed");
        Assert.Equal(3, parsed.Count);
        var byRaw = parsed.ToDictionary(p => p["source_message_key"]!.GetValue<string>(), p => p["payload"]!);
        Assert.Equal("needs_llm", byRaw["s07-live"]["outcome"]!.GetValue<string>());
        Assert.Equal("target_report_without_rule_match", byRaw["s07-live"]["fallback_reason"]!.GetValue<string>());
        Assert.Equal("no_facts", byRaw["s07-stale"]["outcome"]!.GetValue<string>());
        Assert.Equal("llm_skipped_stale", byRaw["s07-stale"]["fallback_reason"]!.GetValue<string>());
        Assert.Equal("no_facts", byRaw["s07-history"]["outcome"]!.GetValue<string>());
        Assert.Equal("llm_skipped_lane", byRaw["s07-history"]["fallback_reason"]!.GetValue<string>());
        foreach (var p in parsed)
        {
            Valid(ParseSchema, p["payload"]!);
        }
        var request = Assert.Single(await EventsAsync("llm.requested"));
        ValidEnvelope(request);
        Valid(LlmSchema, request["payload"]!);
        Assert.Equal(1, request["payload"]!["fencing_token"]!.GetValue<int>());
        Assert.Equal(byRaw["s07-live"]["attempt_id"]!.GetValue<string>(), request["payload"]!["rules_context"]!["attempt_id"]!.GetValue<string>());
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'llm-worker' AND outcome IS NULL")); // waits for P06
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'parse' AND outcome = 'needs_llm' AND outputs->>'llm_request_id' IS NOT NULL"));
        f.Evidence.Record("P05-S07", new { live = "needs_llm", stale = "llm_skipped_stale", history = "llm_skipped_lane", llm_requested = 1 });
    }

    [Fact]
    public async Task S08_Stage_facts_match_the_legacy_processor_field_by_field()
    {
        await f.ResetAsync();
        const string text = "Шахеди на Сумщині курсом на Полтавщину.\nБпЛА на Полтавщині у південному напрямку.";
        await RunAsync(1, ("s08", text, null));
        var stageFacts = Assert.Single(await EventsAsync("parse.completed"))["payload"]!["facts"]!.AsArray();

        // The same raw row through the legacy processor (the shadow's counterpart): targets in the database.
        var rawId = await f.ScalarAsync<long>("SELECT raw_message_id FROM raw_messages");
        var legacyCount = await f.LegacyProcessor.ProcessAsync(rawId, None);
        Assert.Equal(stageFacts.Count, legacyCount);
        await using var db = await f.Factory.CreateDbContextAsync();
        var targets = await db.Targets.AsNoTracking().Include(t => t.LocationPlace).Where(t => t.RawMessageId == rawId).OrderBy(t => t.SegmentIndex).ThenBy(t => t.TargetId).ToListAsync();
        var kinds = f.Indexes.EventKinds;
        var legacyFacts = targets.Select(t => FactMapper.ToFact(t, kinds, f.Indexes.Gazetteer, null, "uk", "rule-0.1")).ToList();
        var stageByIndex = stageFacts.Select(x => x!).OrderBy(x => x["evidence"]!["segment_index"]!.GetValue<int>()).ToList();
        Assert.Equal(legacyFacts.Count, stageByIndex.Count);
        var differences = new List<string>();
        for (var i = 0; i < legacyFacts.Count; i++)
        {
            var legacy = legacyFacts[i];
            var stage = stageByIndex[i];
            foreach (var section in new[] { "event_kind_code", "category", "effective_at", "location", "confidence" })
            {
                if (FactMapper.Canonical(legacy[section]!) != FactMapper.Canonical(stage[section]!))
                {
                    differences.Add($"{i}.{section}: legacy {FactMapper.Canonical(legacy[section]!)} vs stage {FactMapper.Canonical(stage[section]!)}");
                }
            }
            // Attributes: everything the legacy row carries must be in the stage fact (the stage fact also has rule-only
            // extras — hedged, is_launch, places — that the saved row does not keep).
            foreach (var (key, value) in legacy["attributes"]!.AsObject())
            {
                if (key is "language")
                {
                    continue; // not stored on the row (parser_metadata is: both sides are stamped by EventKindIndex.Stamp)
                }
                var stageValue = stage["attributes"]![key];
                if (FactMapper.Canonical(value ?? JsonValue.Create("null")!) != FactMapper.Canonical(stageValue ?? JsonValue.Create("null")!))
                {
                    differences.Add($"{i}.attributes.{key}: legacy {value?.ToJsonString() ?? "null"} vs stage {stageValue?.ToJsonString() ?? "null"}");
                }
            }
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
        Assert.Equal(EventKindLegacyMap.ToCode(targets[0].EventType), stageByIndex[0]["event_kind_code"]!.GetValue<string>());
        f.Evidence.Record("P05-S08", new { facts = stageFacts.Count, legacy_targets = legacyCount, field_differences = differences.Count });
    }
}

internal static class TaskExtensions
{
    public static async Task<TResult> Then<T, TResult>(this Task<T> task, Func<T, TResult> map) => map(await task);
}
