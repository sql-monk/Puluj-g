using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging.Tests.Unit;
using Puluj.Processing.Stages;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P06: llm-worker (lease, fencing, audit, terminal failures) and finalizer (exactly one canonical extraction per raw/run,
/// `observations.recorded`, `message.analysis.completed` for every outcome). The provider is <see cref="FakeLlmCompletion"/>;
/// every assert is on committed rows or outbox events validated against the contract schemas.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class FinalizerTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly JsonSchema Observations = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "observations.recorded.schema.json"));
    private static readonly JsonSchema Analysis = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "message.analysis.completed.schema.json"));
    private static readonly JsonSchema LlmCompleted = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "llm.completed.schema.json"));
    private static readonly JsonSchema LlmFailed = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "llm.failed.schema.json"));
    private const string TargetReport = "Летить щось невідоме, загроза для півдня."; // trigger stems, no rule match → needs_llm (as P05-S07)

    private async Task StartAllAsync(bool llmWorker = true)
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Parser.StartAsync(None);
        if (llmWorker)
        {
            await f.LlmWorker.StartAsync(None);
        }
        await f.Finalizer.StartAsync(None);
    }

    private async Task StopAllAsync()
    {
        await f.Finalizer.StopAsync(None);
        await f.LlmWorker.StopAsync(None);
        await f.Parser.StopAsync(None);
        await f.Normalizer.StopAsync(None);
        await f.Archive.StopAsync(None);
        await f.RawWriter.StopAsync(None);
        await f.Relay.StopAsync(None);
    }

    private async Task<Guid> RunAsync(string id, string? text, int extractions = 1, bool llmWorker = true, JsonDocument? payload = null)
    {
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message(id, text) with { RawPayload = payload }, source, "test", null, live: true, None);
        await StartAllAsync(llmWorker);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions") == extractions, TimeSpan.FromSeconds(60)),
                $"extractions: {await f.CountAsync("processing.extractions")} of {extractions}");
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0)); // the relay published the finalizer's events
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL") == 0)); // and the archive stored them
        }
        finally
        {
            await StopAllAsync();
        }
        return await f.ScalarAsync<Guid>("SELECT run_id FROM processing.runs WHERE lane = 'live' AND state = 'running'");
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
    public async Task F01_Rules_result_becomes_one_canonical_extraction_with_observations_and_both_events()
    {
        await f.ResetAsync();
        await RunAsync("f01", "Шахеди на Сумщині курсом на Полтавщину.");
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'completed' AND method = 'rules' AND extraction_version = 1"));
        var facts = await f.ScalarAsync<int>("SELECT jsonb_array_length(facts) FROM processing.extractions");
        Assert.True(facts >= 1);
        Assert.Equal(facts, await f.CountAsync("processing.observations", "category = 'target' AND event_kind_code = 'target.observed' AND legacy_target_id IS NULL"));
        Assert.Equal(0, await f.CountAsync("targets"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'finalize' AND outcome = 'completed' AND outputs->>'extraction_result_id' IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", "subscription_id = 'finalizer' AND state = 'succeeded' AND stage_result_id IS NOT NULL"));

        var recorded = Assert.Single(await EventsAsync("observations.recorded"));
        ValidEnvelope(recorded);
        Valid(Observations, recorded["payload"]!);
        Assert.Equal(["track-worker"], recorded["payload"]!["expected_branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        var observation = recorded["payload"]!["observations"]!.AsArray()[0]!;
        Assert.Equal(1, await f.CountAsync("processing.observations", $"observation_id = '{observation["observation_id"]!.GetValue<string>()}'"));
        var parsed = Assert.Single(await EventsAsync("parse.completed"));
        Assert.Equal(parsed["event_id"]!.GetValue<string>(), recorded["causation_id"]!.GetValue<string>());

        var analysis = Assert.Single(await EventsAsync("message.analysis.completed"));
        ValidEnvelope(analysis);
        Valid(Analysis, analysis["payload"]!);
        var a = analysis["payload"]!;
        Assert.Equal("completed", a["outcome"]!.GetValue<string>());
        Assert.Equal("rules", a["method"]!.GetValue<string>());
        Assert.Equal(facts, a["fact_count"]!.GetValue<int>());
        Assert.Equal(recorded["payload"]!["extraction_result_id"]!.GetValue<string>(), a["extraction_result_id"]!.GetValue<string>());
        Assert.NotNull(a["timings"]!["received_at"]);
        Assert.NotNull(a["timings"]!["normalized_at"]);
        Assert.NotNull(a["timings"]!["parsed_at"]);
        Assert.NotNull(a["timings"]!["finalized_at"]);
        Assert.Equal("norm-1", a["versions"]!["normalization"]!.GetValue<string>());
        // archive is the only expected consumer of both events (domain workers planned): both are archived
        Assert.Equal(2, await f.CountAsync("messaging.events", "event_type IN ('observations.recorded', 'message.analysis.completed')"));
        f.Evidence.Record("P06-F01", new { extraction = "completed/rules", facts, observations = facts, targets = 0, expected_branches = new[] { "track-worker" }, events = new { recorded = 1, analysis = 1 } });
    }

    [Fact]
    public async Task F02_No_facts_and_unsupported_still_get_an_analysis_event_but_no_observations()
    {
        await f.ResetAsync();
        await RunAsync("f02", "Доброго ранку, друзі! Гарного дня.");
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'no_facts' AND jsonb_array_length(facts) = 0"));
        Assert.Empty(await EventsAsync("observations.recorded"));
        var analysis = Assert.Single(await EventsAsync("message.analysis.completed"));
        Valid(Analysis, analysis["payload"]!);
        Assert.Equal("no_facts", analysis["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal(0, analysis["payload"]!["fact_count"]!.GetValue<int>());
        Assert.Empty(analysis["payload"]!["expected_branches"]!.AsArray());

        await f.ResetAsync();
        await RunAsync("f02-empty", null, payload: JsonDocument.Parse("{\"kind\":\"dev.ingest\"}"));
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'unsupported' AND method = 'none'"));
        var unsupported = Assert.Single(await EventsAsync("message.analysis.completed"));
        Valid(Analysis, unsupported["payload"]!);
        Assert.Equal("unsupported", unsupported["payload"]!["outcome"]!.GetValue<string>());
        f.Evidence.Record("P06-F02", new { no_facts = "analysis only", unsupported = "analysis only", observations_recorded = 0 });
    }

    [Fact]
    public async Task F03_Needs_llm_goes_through_the_worker_and_is_finalized_with_full_audit()
    {
        await f.ResetAsync();
        var run = await RunAsync("f03", TargetReport);
        Assert.Single(f.Llm.Calls);
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'completed' AND method = 'llm' AND array_length(llm_request_ids, 1) = 1"));
        Assert.Equal(1, await f.CountAsync("processing.observations", "event_kind_code = 'target.observed'"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'finalize' AND outcome = 'completed'"));

        var request = Assert.Single(await EventsAsync("llm.requested"));
        var requestId = request["payload"]!["request_id"]!.GetValue<string>();
        var completed = Assert.Single(await EventsAsync("llm.completed"));
        ValidEnvelope(completed);
        Valid(LlmCompleted, completed["payload"]!);
        Assert.Equal(requestId, completed["payload"]!["request_id"]!.GetValue<string>());
        Assert.Equal(1, completed["payload"]!["fencing_token"]!.GetValue<int>());
        Assert.Equal("facts", completed["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal(request["event_id"]!.GetValue<string>(), completed["causation_id"]!.GetValue<string>());
        Assert.StartsWith("llm-worker@", completed["producer"]!.GetValue<string>());

        // Full provenance of the paid call.
        Assert.Equal(1, await f.CountAsync("llm_requests", $"request_id = '{requestId}' AND fencing_token = 1 AND run_id = '{run}' AND outcome = 'applied' AND provider_request_id LIKE 'msg_%' AND attempt_id IS NOT NULL AND estimated_cost_usd > 0 AND response_text IS NOT NULL AND request_text IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.attempts", $"subscription_id = 'llm-worker:job' AND job_key = 'llm:{requestId}' AND state = 'succeeded' AND fencing_token = 1"));
        var analysis = Assert.Single(await EventsAsync("message.analysis.completed"));
        Valid(Analysis, analysis["payload"]!);
        Assert.Equal("llm", analysis["payload"]!["method"]!.GetValue<string>());
        Assert.Equal([requestId], analysis["payload"]!["llm_request_ids"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(analysis["payload"]!["versions"]!["model"]);
        f.Evidence.Record("P06-F03", new { llm_calls = f.Llm.Calls.Count, extraction = "completed/llm", audit = new { request_id = true, fencing_token = 1, run_id = true, outcome = "applied", provider_request_id = true }, observations = 1 });
    }

    [Fact]
    public async Task F04_W8_Late_result_with_a_stale_fencing_token_is_a_noop_in_the_worker_and_in_the_finalizer()
    {
        await f.ResetAsync();
        // Worker side: the call is slow (gate closed); meanwhile another replica takes the job over (token 2).
        f.Llm.Gate = new SemaphoreSlim(0);
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message("f04", TargetReport), source, "test", null, live: true, None);
        await StartAllAsync();
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(!f.Llm.Calls.IsEmpty), TimeSpan.FromSeconds(40)));
            var requestId = await f.ScalarAsync<Guid>("SELECT event_id FROM processing.attempts WHERE subscription_id = 'llm-worker:job' AND fencing_token = 1");
            var jobKey = LlmWorkerHandler.JobKey(requestId);
            // Takeover by "another replica" after the lease expired: token 2 exists before our answer arrives.
            await f.ExecAsync("INSERT INTO processing.attempts (subscription_id, event_id, job_key, worker, state, fencing_token, started_at) VALUES ('llm-worker:job', @e, @k, 'llm-worker@replica-2', 'succeeded', 2, now())", ("e", requestId), ("k", jobKey));
            f.Llm.Gate.Release();
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'llm-worker' AND outcome = 'noop'") == 1, TimeSpan.FromSeconds(40)));
            Assert.Empty(await EventsAsync("llm.completed"));
            Assert.Equal(1, await f.CountAsync("llm_requests", $"request_id = '{requestId}' AND fencing_token = 1 AND outcome = 'late'")); // the paid call is on record
            Assert.Equal(1, await f.CountAsync("processing.attempts", $"job_key = '{jobKey}' AND fencing_token = 1 AND state = 'superseded'"));

            // Finalizer side: a synthetic llm.completed carrying the stale token 1 must not become the extraction.
            var stale = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'llm.requested'"))!.AsObject();
            stale["event_id"] = Guid.CreateVersion7().ToString();
            stale["event_type"] = "llm.completed";
            stale["producer"] = "llm-worker@replica-1";
            stale["payload"] = new JsonObject
            {
                ["raw_message_id"] = stale["raw_message_id"]!.GetValue<long>(),
                ["request_id"] = requestId.ToString(),
                ["fencing_token"] = 1,
                ["model"] = "fake",
                ["prompt_version"] = "1",
                ["outcome"] = "facts",
                ["facts"] = new JsonArray(),
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                ["duration_ms"] = 1,
            };
            await f.PublishRawAsync("puluj.live.llm.completed", Encoding.UTF8.GetBytes(stale.ToJsonString()), stale["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'finalizer' AND outcome = 'noop' AND reason LIKE 'fencing token 1 < current 2%'") == 1, TimeSpan.FromSeconds(40)));
            Assert.Equal(0, await f.CountAsync("processing.extractions"));
            Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'finalize' AND outcome = 'awaiting_llm'"));
        }
        finally
        {
            await StopAllAsync();
        }
        f.Evidence.Record("P06-F04", new { window = "W8", worker_late_noop = 1, audit_outcome = "late", finalizer_late_noop = 1, extractions = 0 });
    }

    [Fact]
    public async Task F05_Retryable_provider_failures_end_in_a_final_llm_failed_and_a_failed_extraction()
    {
        await f.ResetAsync();
        f.Llm.Fail("provider_timeout", retryable: true).Fail("provider_error", retryable: true, System.Net.HttpStatusCode.ServiceUnavailable);
        await RunAsync("f05", TargetReport);
        Assert.Equal(2, f.Llm.Calls.Count); // Llm:MaxAttempts = 2
        var failed = Assert.Single(await EventsAsync("llm.failed"));
        ValidEnvelope(failed);
        Valid(LlmFailed, failed["payload"]!);
        Assert.True(failed["payload"]!["final"]!.GetValue<bool>());
        Assert.Equal(2, failed["payload"]!["attempts"]!.GetValue<int>());
        Assert.Equal("provider_error", failed["payload"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'failed' AND method = 'llm' AND error->>'code' = 'provider_error'"));
        Assert.Equal(2, await f.CountAsync("llm_requests", "outcome IN ('provider_timeout', 'provider_error') AND fencing_token IN (1, 2)"));
        Assert.Equal(2, await f.CountAsync("processing.attempts", "subscription_id = 'llm-worker:job' AND state = 'failed'"));
        var analysis = Assert.Single(await EventsAsync("message.analysis.completed"));
        Valid(Analysis, analysis["payload"]!);
        Assert.Equal("failed", analysis["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal("provider_error", analysis["payload"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, await f.CountAsync("processing.quarantine"));
        f.Evidence.Record("P06-F05", new { provider_calls = f.Llm.Calls.Count, llm_failed_final = true, attempts = 2, extraction = "failed", quarantine = 0 });
    }

    [Fact]
    public async Task F06_Duplicate_terminal_events_and_two_finalizer_replicas_keep_one_extraction()
    {
        await f.ResetAsync();
        await RunAsync("f06", "Шахеди на Сумщині курсом на Полтавщину.");
        var parsed = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'parse.completed'"))!.AsObject();
        var replica = f.NewConsumer(f.Services.GetRequiredService<FinalizerHandler>(), "finalizer@replica-2");
        await f.Finalizer.StartAsync(None);
        await replica.StartAsync(None);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                parsed["event_id"] = Guid.CreateVersion7().ToString(); // a republished/duplicated terminal input
                await f.PublishRawAsync("puluj.live.parse.completed", Encoding.UTF8.GetBytes(parsed.ToJsonString()), parsed["event_id"]!.GetValue<string>());
            }
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'finalizer' AND outcome = 'noop'") == 3, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await f.Finalizer.StopAsync(None);
            await replica.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("processing.extractions"));
        Assert.Single(await EventsAsync("message.analysis.completed"));
        Assert.Single(await EventsAsync("observations.recorded"));
        Assert.Equal(3, f.Finalizer.Delivered + replica.Delivered - 1);
        f.Evidence.Record("P06-F06", new { duplicate_inputs = 3, finalizer_noops = 3, extractions = 1, replicas = new { r1 = f.Finalizer.Delivered, r2 = replica.Delivered } });
    }

    [Fact]
    public async Task F07_Non_retryable_provider_error_and_refusal_are_terminal_after_one_call()
    {
        await f.ResetAsync();
        f.Llm.Fail("provider_error", retryable: false, System.Net.HttpStatusCode.BadRequest);
        await RunAsync("f07", TargetReport);
        Assert.Single(f.Llm.Calls);
        var failed = Assert.Single(await EventsAsync("llm.failed"));
        Assert.Equal(1, failed["payload"]!["attempts"]!.GetValue<int>());
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'failed'"));

        await f.ResetAsync();
        f.Llm.Refuse();
        await RunAsync("f07-refusal", TargetReport);
        var completed = Assert.Single(await EventsAsync("llm.completed"));
        Valid(LlmCompleted, completed["payload"]!);
        Assert.Equal("needs_review", completed["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal(1, await f.CountAsync("processing.extractions", "outcome = 'needs_review' AND jsonb_array_length(facts) = 0"));
        Assert.Empty(await EventsAsync("observations.recorded"));
        var analysis = Assert.Single(await EventsAsync("message.analysis.completed"));
        Assert.Equal("needs_review", analysis["payload"]!["outcome"]!.GetValue<string>());
        Assert.Equal(1, await f.CountAsync("llm_requests", "outcome = 'applied' AND response_text IS NULL AND provider_request_id = 'msg_refused'"));
        f.Evidence.Record("P06-F07", new { non_retryable = "final after 1 call", refusal = "needs_review, no observations" });
    }

    [Fact]
    public async Task F08_Llm_completed_before_parse_completed_wins_and_the_late_parse_is_a_noop()
    {
        await f.ResetAsync();
        // Stage 1: run without the llm-worker so the request sits in its queue and the finalizer is awaiting.
        var source = await f.SourceAsync();
        await f.Ingress.PublishAsync(f.Message("f08", TargetReport), source, "test", null, live: true, None);
        await StartAllAsync(llmWorker: false);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.stage_results", "stage = 'finalize' AND outcome = 'awaiting_llm'") == 1, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await StopAllAsync();
        }
        // Stage 2: a fresh run of the same raw would be a different run; here we simulate the other queue order by
        // finalizing from a synthetic llm.completed (token 1, no job row → current 0) and then re-delivering parse.completed.
        var request = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'llm.requested'"))!.AsObject();
        var completed = request.DeepClone().AsObject();
        completed["event_id"] = Guid.CreateVersion7().ToString();
        completed["event_type"] = "llm.completed";
        completed["producer"] = "llm-worker@replica-2";
        completed["payload"] = new JsonObject
        {
            ["raw_message_id"] = request["raw_message_id"]!.GetValue<long>(),
            ["request_id"] = request["payload"]!["request_id"]!.GetValue<string>(),
            ["fencing_token"] = 1,
            ["model"] = "fake",
            ["prompt_version"] = "1",
            ["outcome"] = "no_facts",
            ["facts"] = new JsonArray(),
            ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
            ["duration_ms"] = 1,
        };
        var parsed = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'parse.completed'"))!.AsObject();
        parsed["event_id"] = Guid.CreateVersion7().ToString();
        await f.Finalizer.StartAsync(None);
        try
        {
            await f.PublishRawAsync("puluj.live.llm.completed", Encoding.UTF8.GetBytes(completed.ToJsonString()), completed["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions", "outcome = 'no_facts' AND method = 'llm'") == 1, TimeSpan.FromSeconds(40)));
            await f.PublishRawAsync("puluj.live.parse.completed", Encoding.UTF8.GetBytes(parsed.ToJsonString()), parsed["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'finalizer' AND outcome = 'noop'") == 1, TimeSpan.FromSeconds(40)));
        }
        finally
        {
            await f.Finalizer.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("processing.extractions"));
        Assert.Equal(1, await f.CountAsync("processing.stage_results", "stage = 'finalize' AND outcome = 'no_facts'")); // awaiting_llm → terminal, never back
        f.Evidence.Record("P06-F08", new { order = "llm.completed before parse.completed{needs_llm}", extractions = 1, late_parse = "noop" });
    }
}
