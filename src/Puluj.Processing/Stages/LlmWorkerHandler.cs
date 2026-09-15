using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Puluj.Domain.Entities;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Indexes;
using Puluj.Processing.Llm;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Processing.Stages;

/// <summary>
/// The `llm-worker` subscription (plan §4, ADR-0004 §6 п.6 / W8): executes the `llm.requested` command against the model
/// under a persisted lease with a fencing token, keeps a durable audit of every paid call, and answers with
/// `llm.completed` or a terminal `llm.failed{final:true}`. The provider call runs outside any transaction; the
/// transaction only records the outcome and the outgoing event. Retries of transient provider failures are job-level
/// (`processing.attempts` rows with `subscription_id = llm-worker:job`, one per fencing token) and always end in a
/// terminal event before the broker's delivery limit could quarantine the command.
/// </summary>
public sealed class LlmWorkerHandler(
    IDbContextFactory<PulujDbContext> factory,
    ILlmCompletion completion,
    LlmParser parser,
    INormalizer normalizer,
    TargetBuilder builder,
    IndexProvider indexes,
    LlmBreaker breaker,
    IOptionsMonitor<LlmOptions> options,
    PulujMetrics metrics,
    TimeProvider clock,
    ILogger<LlmWorkerHandler> logger) : IDeliveryHandler
{
    public const string Subscription = "llm-worker";
    /// <summary>`subscription_id` of the job (lease) rows, separate from the consumer's delivery attempts.</summary>
    public const string JobSubscription = "llm-worker:job";
    public const string CompletedEventType = "llm.completed";
    public const string FailedEventType = "llm.failed";
    public const string SchemaVersion = "1.0";

    private readonly RateLimiter _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, options.CurrentValue.MaxCallsPerMinute),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Job(string Key, long AttemptId, int Token);

    /// <summary>Terminal or successful outcome carried from the call (outside the tx) into the short transaction.</summary>
    private sealed record Prepared(Guid RequestId, long RawMessageId, Job? Job, string? FailureCode, string? FailureMessage, bool FailureRetryable, int JobAttempts,
        string? Outcome, JsonArray Facts, LlmCompletionResult? Result, long? AuditId, long DurationMs, string Model, string PromptVersion);

    public static string JobKey(Guid requestId) => $"llm:{requestId}";

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", "llm.requested without payload");
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "llm.requested without raw_message_id");
        var requestId = Guid.TryParse(payload["request_id"]?.GetValue<string>(), out var r) ? r : throw new PermanentDeliveryException("invalid_payload", "llm.requested without request_id");
        var o = options.CurrentValue;
        var model = payload["model"]?.GetValue<string>() ?? o.Model;
        var promptVersion = payload["prompt_version"]?.GetValue<string>() ?? o.PromptVersion;
        var maxOutput = payload["budget"]?["max_output_tokens"]?.GetValue<int>() ?? o.MaxOutputTokens;
        var deadline = DateTimeOffset.TryParse(payload["deadline_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
        var jobKey = JobKey(requestId);

        Prepared Terminal(string code, string message, int attempts, Job? job = null) =>
            new(requestId, rawId, job, code, message, false, attempts, null, [], null, null, sw.ElapsedMilliseconds, model, promptVersion);

        await indexes.Ready.WaitAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        // A command that already ended (its terminal llm.failed was committed) is not retried on redelivery.
        var failedSoFar = await CountJobAttemptsAsync(conn, jobKey, "failed", ct);
        if (failedSoFar >= o.MaxAttempts)
        {
            return Terminal("attempts_exhausted", $"{failedSoFar} provider attempts failed", failedSoFar);
        }
        if (deadline is { } dl && clock.GetUtcNow() > dl)
        {
            return Terminal("deadline_exceeded", $"deadline {dl:O} passed before the model was asked", failedSoFar);
        }

        // Input: the normalized text of the raw row, checked against what the parser saw.
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found");
        var normalized = normalizer.Normalize(raw.RawText ?? "");
        var input = payload["input"]?.AsObject();
        var expectedVersion = input?["normalization_version"]?.GetValue<string>();
        var expectedHash = input?["normalized_text_hash"]?.GetValue<string>();
        if (expectedVersion is not null && expectedVersion != Normalizer.Version)
        {
            throw new InvalidOperationException($"normalization_version {expectedVersion} vs this build {Normalizer.Version}"); // rolling deploy: retry
        }
        if (expectedHash is not null && expectedHash != StageSupport.Sha256(normalized.Text))
        {
            return Terminal("normalization_drift", "normalized text differs from the parser's input", failedSoFar);
        }

        // Lease (W8): one live holder per request; a redelivery while another replica holds it waits for the lease to lapse.
        var job = await AcquireLeaseAsync(conn, jobKey, requestId, o, ct);
        if (job is null)
        {
            throw new InvalidOperationException($"lease for {jobKey} still held by another replica"); // transient: consumer backoff, no charge
        }

        // Budget: a tripped breaker is terminal (the finalizer must not wait 15 minutes); the per-replica rate limit is waited out.
        if (breaker.IsOpen(clock.GetUtcNow(), out var pause))
        {
            await FinishJobAsync(conn, job.AttemptId, "failed", $"budget_unavailable: {pause}", ct);
            return Terminal("budget_unavailable", $"model paused: {pause}", failedSoFar + 1, job);
        }
        using var permit = await _limiter.AcquireAsync(1, ct);
        if (!permit.IsAcquired)
        {
            await FinishJobAsync(conn, job.AttemptId, "interrupted", "rate limit permit not granted", ct);
            throw new InvalidOperationException("rate limit permit not granted"); // transient, not a paid attempt
        }

        var started = clock.GetUtcNow();
        breaker.Attempt();
        LlmCompletionResult result;
        try
        {
            result = await completion.CompleteAsync(new LlmCompletionRequest(normalized.Text, model, promptVersion, maxOutput, TimeSpan.FromSeconds(o.TimeoutSeconds)), ct);
        }
        catch (LlmCompletionException ex)
        {
            metrics.LlmCall(ex.Code);
            breaker.Fail();
            if (ex.StatusCode is { } status && breaker.Trip(status, ex.Message, clock.GetUtcNow()) is { } paused)
            {
                logger.LogWarning("LLM provider answered {Status} ({Code}); model paused for {Pause}", (int)status, ex.Code, paused);
            }
            await AuditAsync(conn, raw, envelope, requestId, job, model, promptVersion, normalized.Text, null, ex.Code, (int?)ex.StatusCode, ex.Message, 0, started, ct);
            await FinishJobAsync(conn, job.AttemptId, "failed", $"{ex.Code}: {ex.Message}", ct);
            var attempts = failedSoFar + 1;
            if (ex.Retryable && attempts < o.MaxAttempts)
            {
                throw new InvalidOperationException($"LLM call failed ({ex.Code}), attempt {attempts}/{o.MaxAttempts}: {ex.Message}", ex); // transient: redelivery → next attempt
            }
            return Terminal(ex.Code, ex.Message, attempts, job);
        }
        breaker.Reset();

        // The paid call is durable before anything else can fail (review B4): audit first, outcome later.
        var facts = new JsonArray();
        var outcome = "needs_review";
        if (!result.Refused && result.ResponseJson is not null)
        {
            var kinds = indexes.EventKinds;
            var mapped = parser.MapJson(result.ResponseJson, normalized);
            var targets = mapped.Select(f => (Fact: f, Target: builder.Build(f, raw, raw.Source!, f.ParserVersion, f.Method, normalized.Language))).ToList();
            kinds.Stamp(targets.Select(t => t.Target));
            foreach (var t in targets)
            {
                facts.Add(FactMapper.ToFact(t.Target, kinds, indexes.Gazetteer, t.Fact, normalized.Language, t.Fact.ParserVersion));
            }
            outcome = facts.Count > 0 ? "facts" : "no_facts";
        }
        metrics.LlmCall(result.Refused ? "refusal" : outcome);
        var auditId = await AuditAsync(conn, raw, envelope, requestId, job, model, promptVersion, normalized.Text, result, "answered", null, null, facts.Count, started, ct);
        return new Prepared(requestId, rawId, job, null, null, false, failedSoFar, outcome, facts, result, auditId, sw.ElapsedMilliseconds, model, promptVersion);
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var p = (Prepared)state!;
        var jobKey = JobKey(p.RequestId);
        if (p.Job is not null)
        {
            // Fencing (W8): a newer token means another replica took the job over while we were waiting for the model.
            var current = await MaxTokenAsync(conn, tx, jobKey, ct);
            if (current != p.Job.Token)
            {
                await FinishJobAsync(conn, tx, p.Job.AttemptId, "superseded", $"late result: token {p.Job.Token}, current {current}", ct);
                if (p.AuditId is long lateAudit)
                {
                    await SetAuditOutcomeAsync(conn, tx, lateAudit, "late", ct);
                }
                metrics.LlmCall("late");
                return DeliveryResult.Noop($"late result: fencing token {p.Job.Token} superseded by {current}");
            }
        }
        if (p.FailureCode is not null)
        {
            var failed = StageSupport.Child(envelope, FailedEventType, SchemaVersion, Producer, clock.GetUtcNow(), new JsonObject
            {
                ["raw_message_id"] = p.RawMessageId,
                ["request_id"] = p.RequestId.ToString(),
                ["fencing_token"] = p.Job?.Token ?? 1,
                ["error"] = new JsonObject { ["code"] = p.FailureCode, ["message"] = p.FailureMessage, ["retryable"] = false },
                ["attempts"] = p.JobAttempts,
                ["final"] = true,
            });
            return new DeliveryResult("completed", p.FailureCode, [failed]);
        }
        await FinishJobAsync(conn, tx, p.Job!.AttemptId, "succeeded", null, ct);
        await SetAuditOutcomeAsync(conn, tx, p.AuditId!.Value, "applied", ct);
        var r = p.Result!;
        var usage = new JsonObject
        {
            ["input_tokens"] = r.InputTokens,
            ["output_tokens"] = r.OutputTokens,
            ["cache_creation_input_tokens"] = r.CacheCreationInputTokens,
            ["cache_read_input_tokens"] = r.CacheReadInputTokens,
            ["cost_usd"] = (double)LlmCost.Calculate(options.CurrentValue, r.InputTokens, r.CacheCreationInputTokens, r.CacheReadInputTokens, r.OutputTokens),
        };
        var completed = StageSupport.Child(envelope, CompletedEventType, SchemaVersion, Producer, clock.GetUtcNow(), new JsonObject
        {
            ["raw_message_id"] = p.RawMessageId,
            ["request_id"] = p.RequestId.ToString(),
            ["provider_request_id"] = r.ProviderRequestId,
            ["fencing_token"] = p.Job.Token,
            ["model"] = p.Model,
            ["prompt_version"] = p.PromptVersion,
            ["outcome"] = p.Outcome,
            ["facts"] = p.Facts.DeepClone(),
            ["usage"] = usage,
            ["duration_ms"] = p.DurationMs,
            ["audit_id"] = p.AuditId,
        });
        return new DeliveryResult("completed", null, [completed]);
    }

    // ---- lease / audit SQL ----

    /// <summary>
    /// Takes the lease of a job: under an advisory transaction lock on the key, a live lease of another holder is waited
    /// out (bounded by the lease itself) and then the next fencing token is inserted; null when it is still held.
    /// </summary>
    private async Task<Job?> AcquireLeaseAsync(NpgsqlConnection conn, string jobKey, Guid requestId, LlmOptions o, CancellationToken ct)
    {
        for (var round = 0; round < 3; round++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var l = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx))
            {
                l.Parameters.AddWithValue("k", jobKey);
                await l.ExecuteNonQueryAsync(ct);
            }
            int maxToken = 0;
            DateTimeOffset? heldUntil = null;
            await using (var q = new NpgsqlCommand(
                "SELECT fencing_token, state, lease_until FROM processing.attempts WHERE subscription_id = @s AND job_key = @k ORDER BY fencing_token DESC LIMIT 1", conn, tx))
            {
                q.Parameters.AddWithValue("s", JobSubscription);
                q.Parameters.AddWithValue("k", jobKey);
                await using var reader = await q.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    maxToken = reader.GetInt32(0);
                    if (reader.GetString(1) == "running" && !reader.IsDBNull(2))
                    {
                        var leaseEnd = reader.GetFieldValue<DateTimeOffset>(2);
                        heldUntil = leaseEnd > clock.GetUtcNow() ? leaseEnd : null;
                    }
                }
            }
            if (heldUntil is { } until)
            {
                await tx.RollbackAsync(ct);
                var wait = until - clock.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    logger.LogInformation("Lease {Job} held until {Until:O}; waiting {Wait}", jobKey, until, wait);
                    await Task.Delay(wait + TimeSpan.FromMilliseconds(100), ct);
                }
                continue;
            }
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO processing.attempts (subscription_id, event_id, job_key, worker, state, fencing_token, lease_until, started_at)
                VALUES (@s, @e, @k, @w, 'running', @token, now() + @lease, now())
                RETURNING attempt_id
                """, conn, tx);
            insert.Parameters.AddWithValue("s", JobSubscription);
            insert.Parameters.AddWithValue("e", requestId);
            insert.Parameters.AddWithValue("k", jobKey);
            insert.Parameters.AddWithValue("w", Producer);
            insert.Parameters.AddWithValue("token", maxToken + 1);
            insert.Parameters.AddWithValue("lease", TimeSpan.FromSeconds(Math.Max(o.TimeoutSeconds + 5, o.LeaseSeconds)));
            var attemptId = (long)(await insert.ExecuteScalarAsync(ct))!;
            await tx.CommitAsync(ct);
            return new Job(jobKey, attemptId, maxToken + 1);
        }
        return null;
    }

    private static async Task<int> MaxTokenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string jobKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT coalesce(max(fencing_token), 0) FROM processing.attempts WHERE subscription_id = @s AND job_key = @k", conn, tx);
        cmd.Parameters.AddWithValue("s", JobSubscription);
        cmd.Parameters.AddWithValue("k", jobKey);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<int> CountJobAttemptsAsync(NpgsqlConnection conn, string jobKey, string state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM processing.attempts WHERE subscription_id = @s AND job_key = @k AND state = @state", conn);
        cmd.Parameters.AddWithValue("s", JobSubscription);
        cmd.Parameters.AddWithValue("k", jobKey);
        cmd.Parameters.AddWithValue("state", state);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static Task FinishJobAsync(NpgsqlConnection conn, long attemptId, string state, string? error, CancellationToken ct) => FinishJobAsync(conn, null, attemptId, state, error, ct);

    private static async Task FinishJobAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long attemptId, string state, string? error, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE processing.attempts SET state = @state, error = @error, finished_at = now(), lease_until = NULL WHERE attempt_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("state", state);
        cmd.Parameters.AddWithValue("error", (object?)(error is { Length: > 4000 } ? error[..4000] : error) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", attemptId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Durable record of the call (autocommit, before the result transaction): the paid request survives any later failure.</summary>
    private async Task<long> AuditAsync(NpgsqlConnection conn, RawMessage raw, Envelope envelope, Guid requestId, Job job, string model, string promptVersion, string requestText,
        LlmCompletionResult? result, string outcome, int? statusCode, string? error, int factsCount, DateTimeOffset started, CancellationToken ct)
    {
        var o = options.CurrentValue;
        decimal? cost = result is null ? null : LlmCost.Calculate(o, result.InputTokens, result.CacheCreationInputTokens, result.CacheReadInputTokens, result.OutputTokens);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO llm_requests (raw_message_id, source_id, occurred_at, worker, model, prompt_version, outcome, status_code, duration_ms,
                input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens, estimated_cost_usd, facts_count,
                request_text, system_prompt, response_text, error, request_id, run_id, fencing_token, attempt_id, provider_request_id)
            VALUES (@raw, @source, now(), @worker, @model, @prompt, @outcome, @status, @duration, @input, @cw, @cr, @output, @cost, @facts,
                @text, @system, @response, @error, @request, @run, @token, @attempt, @provider)
            RETURNING llm_request_id
            """, conn);
        cmd.Parameters.AddWithValue("raw", raw.RawMessageId);
        cmd.Parameters.AddWithValue("source", raw.SourceId);
        cmd.Parameters.AddWithValue("worker", Producer);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("prompt", promptVersion);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("status", (object?)statusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("duration", (int)Math.Min((clock.GetUtcNow() - started).TotalMilliseconds, int.MaxValue));
        cmd.Parameters.AddWithValue("input", (object?)result?.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cw", (object?)result?.CacheCreationInputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cr", (object?)result?.CacheReadInputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("output", (object?)result?.OutputTokens ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("cost", NpgsqlDbType.Numeric) { Value = (object?)cost ?? DBNull.Value });
        cmd.Parameters.AddWithValue("facts", factsCount);
        cmd.Parameters.AddWithValue("text", requestText);
        cmd.Parameters.AddWithValue("system", parser.SystemPrompt);
        cmd.Parameters.AddWithValue("response", (object?)result?.ResponseJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("request", requestId);
        cmd.Parameters.AddWithValue("run", envelope.ProcessingRunId);
        cmd.Parameters.AddWithValue("token", job.Token);
        cmd.Parameters.AddWithValue("attempt", job.AttemptId);
        cmd.Parameters.AddWithValue("provider", (object?)result?.ProviderRequestId ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task SetAuditOutcomeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long auditId, string outcome, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE llm_requests SET outcome = @o WHERE llm_request_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("o", outcome);
        cmd.Parameters.AddWithValue("id", auditId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
