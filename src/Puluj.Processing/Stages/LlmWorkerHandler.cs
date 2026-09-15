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
/// terminal event before the broker's delivery limit could quarantine the command. Every terminal event carries the
/// token of a job row of its own, so the finalizer's fencing check never drops it.
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
    ILogger<LlmWorkerHandler> logger) : IDeliveryHandler, IDisposable
{
    public const string Subscription = "llm-worker";
    /// <summary>`subscription_id` of the job (lease) rows, separate from the consumer's delivery attempts.</summary>
    public const string JobSubscription = "llm-worker:job";
    public const string CompletedEventType = "llm.completed";
    public const string FailedEventType = "llm.failed";
    public const string SchemaVersion = "1.0";
    private const int WorkerColumnLength = 64; // llm_requests.worker

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

    /// <summary>
    /// Outcome carried from the call (outside the tx) into the short transaction: a result to publish, a terminal failure
    /// (its job row already `failed`), or nothing to do (<see cref="Job"/> and <see cref="FailureCode"/> both null: the
    /// job was completed under another delivery of the same command).
    /// </summary>
    private sealed record Prepared(Guid RequestId, long RawMessageId, Job? Job, string? FailureCode, string? FailureMessage, int JobAttempts,
        string? Outcome, JsonArray Facts, LlmCompletionResult? Result, long? AuditId, long DurationMs, string Model, string PromptVersion, string? NoopReason = null);

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

        Prepared Terminal(string code, string message, int attempts, Job job, long? auditId = null) =>
            new(requestId, rawId, job, code, message, attempts, null, [], null, auditId, sw.ElapsedMilliseconds, model, promptVersion);

        await indexes.Ready.WaitAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        // A job that already succeeded (its llm.completed was committed under another delivery of the same command —
        // a duplicate publish, or our own deferred late result) is not called again: the receipt is a noop.
        Prepared Done() => new(requestId, rawId, null, null, null, 0, null, [], null, null, sw.ElapsedMilliseconds, model, promptVersion, "job already completed under another delivery");
        if (await CountJobAttemptsAsync(conn, jobKey, "succeeded", ct) > 0)
        {
            return Done();
        }

        // Terminal before any call: a job row of its own (review B1/B2) so the event carries the current fencing token and attempts ≥ 1.
        async Task<Prepared> TerminalWithJobAsync(string code, string message)
        {
            var (job, completed) = await AcquireLeaseAsync(conn, jobKey, requestId, o, ct);
            if (completed)
            {
                return Done();
            }
            if (job is null)
            {
                throw new InvalidOperationException($"lease for {jobKey} still held by another replica");
            }
            await FinishJobAsync(conn, job.AttemptId, "failed", $"{code}: {message}", ct);
            return Terminal(code, message, await CountJobAttemptsAsync(conn, jobKey, "failed", ct), job);
        }

        var failedSoFar = await CountJobAttemptsAsync(conn, jobKey, "failed", ct);
        if (failedSoFar >= o.MaxAttempts)
        {
            return await TerminalWithJobAsync("attempts_exhausted", $"{failedSoFar} provider attempts failed");
        }
        if (deadline is { } dl && clock.GetUtcNow() > dl)
        {
            return await TerminalWithJobAsync("deadline_exceeded", $"deadline {dl:O} passed before the model was asked");
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
            return await TerminalWithJobAsync("normalization_drift", "normalized text differs from the parser's input");
        }

        // Budget before the lease (review N1/Q1): the per-replica rate limit and a tripped breaker are waited out within
        // the deadline, so a lease is never taken for a call that would already be late; a pause past the deadline is terminal.
        using var permit = await AcquirePermitAsync(deadline, ct);
        if (permit is null)
        {
            return await TerminalWithJobAsync("deadline_exceeded", "deadline passed while waiting for a rate-limit permit");
        }
        if (breaker.IsOpen(clock.GetUtcNow(), out var pauseReason) && breaker.PausedUntil is { } pausedUntil)
        {
            var pause = pausedUntil - clock.GetUtcNow();
            if (deadline is { } dlp && pausedUntil > dlp)
            {
                return await TerminalWithJobAsync("budget_unavailable", $"model paused for {pause} ({pauseReason}), past the deadline {dlp:O}");
            }
            logger.LogInformation("Model paused for {Pause} ({Reason}); llm request {Request} waits", pause, pauseReason, requestId);
            await Task.Delay(pause + TimeSpan.FromMilliseconds(100), ct);
        }

        // Lease (W8): one live holder per request; a redelivery while another replica holds it waits for the lease to lapse.
        var (lease, alreadyCompleted) = await AcquireLeaseAsync(conn, jobKey, requestId, o, ct);
        if (alreadyCompleted)
        {
            return Done(); // the holder we waited for published the result
        }
        var job = lease ?? throw new InvalidOperationException($"lease for {jobKey} still held by another replica"); // transient: consumer backoff, no charge
        if (deadline is { } dlz && clock.GetUtcNow() > dlz)
        {
            await FinishJobAsync(conn, job.AttemptId, "failed", "deadline_exceeded: passed while waiting for budget", ct);
            return Terminal("deadline_exceeded", $"deadline {dlz:O} passed while waiting for budget", failedSoFar + 1, job);
        }

        var started = clock.GetUtcNow();
        breaker.Attempt();
        LlmCompletionResult result;
        try
        {
            result = await completion.CompleteAsync(new LlmCompletionRequest(normalized.Text, model, promptVersion, maxOutput, TimeSpan.FromSeconds(o.TimeoutSeconds)), ct);
        }
        catch (OperationCanceledException)
        {
            await TryFinishJobAsync(conn, job.AttemptId, "interrupted", "cancelled during the provider call (shutdown)"); // best effort; else the lease lapses
            throw;
        }
        catch (LlmCompletionException ex)
        {
            metrics.LlmCall(ex.Code);
            if (ex.StatusCode is { } status)
            {
                if (breaker.Trip(status, ex.Message, clock.GetUtcNow()) is { } paused) // Trip counts the failure itself (review N3)
                {
                    logger.LogWarning("LLM provider answered {Status} ({Code}); model paused for {Pause}", (int)status, ex.Code, paused);
                }
            }
            else
            {
                breaker.Fail();
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

        // The paid call is durable before anything else can fail (review B4): audit first, mapping after; a response the
        // mapper cannot use is a terminal `invalid_response`, never a second paid call.
        var auditId = await AuditAsync(conn, raw, envelope, requestId, job, model, promptVersion, normalized.Text, result, "answered", null, null, 0, started, ct);
        var facts = new JsonArray();
        var outcome = "needs_review";
        if (!result.Refused && result.ResponseJson is not null)
        {
            try
            {
                var kinds = indexes.EventKinds;
                var mapped = parser.MapJson(result.ResponseJson, normalized);
                var targets = mapped.Select(f => (Fact: f, Target: builder.Build(f, raw, raw.Source!, f.ParserVersion, f.Method, normalized.Language))).ToList();
                kinds.Stamp(targets.Select(t => t.Target));
                foreach (var t in targets)
                {
                    facts.Add(FactMapper.ToFact(t.Target, kinds, indexes.Gazetteer, t.Fact, normalized.Language, t.Fact.ParserVersion));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "LLM response for request {Request} could not be mapped to facts", requestId);
                metrics.LlmCall("invalid_response");
                await SetAuditOutcomeAsync(conn, null, auditId, "invalid_response", null, ct);
                await FinishJobAsync(conn, job.AttemptId, "failed", $"invalid_response: {ex.Message}", ct);
                return Terminal("invalid_response", $"response could not be mapped: {ex.Message}", failedSoFar + 1, job, auditId);
            }
            outcome = facts.Count > 0 ? "facts" : "no_facts";
        }
        metrics.LlmCall(result.Refused ? "refusal" : outcome);

        // Fencing (W8) right after the paid call, outside any transaction: the lease may have lapsed and been taken over
        // while we waited for the model. The late result is recorded here (job `superseded`, audit `late`) whatever the
        // consumer does with the delivery next — a same-event duplicate ends on the inbox conflict, never in ApplyAsync.
        if (await MaxTokenAsync(conn, null, jobKey, ct) is var current && current != job.Token)
        {
            await RecordLateAsync(conn, job, auditId, current, ct);
            if (await HolderRunningAsync(conn, jobKey, ct))
            {
                // The new holder has not published yet: keep the inbox row of this event free for it (review B5).
                throw new DeliveryDeferredException($"late result: fencing token {job.Token} superseded by {current}, still running");
            }
            return Done();
        }
        return new Prepared(requestId, rawId, job, null, null, failedSoFar, outcome, facts, result, auditId, sw.ElapsedMilliseconds, model, promptVersion);
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var p = (Prepared)state!;
        var jobKey = JobKey(p.RequestId);
        if (p.Job is null)
        {
            return DeliveryResult.Noop(p.NoopReason ?? "nothing to apply");
        }

        // Fencing (W8) once more, serialized with lease takeovers under the same advisory lock (review N2): a takeover
        // between the check in PrepareAsync and this transaction. The late result is recorded on a side connection
        // (this transaction is rolled back) and the delivery is deferred (review B5).
        await using (var l = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx))
        {
            l.Parameters.AddWithValue("k", jobKey);
            await l.ExecuteNonQueryAsync(ct);
        }
        var current = await MaxTokenAsync(conn, tx, jobKey, ct);
        if (current != p.Job.Token)
        {
            await using var side = await factory.CreateDbContextAsync(ct);
            var sideConn = (NpgsqlConnection)side.Database.GetDbConnection();
            await sideConn.OpenAsync(ct);
            await RecordLateAsync(sideConn, p.Job, p.AuditId, current, ct);
            throw new DeliveryDeferredException($"late result: fencing token {p.Job.Token} superseded by {current}");
        }

        if (p.FailureCode is not null)
        {
            var failed = StageSupport.Child(envelope, FailedEventType, SchemaVersion, Producer, clock.GetUtcNow(), new JsonObject
            {
                ["raw_message_id"] = p.RawMessageId,
                ["request_id"] = p.RequestId.ToString(),
                ["fencing_token"] = p.Job.Token,
                ["error"] = new JsonObject { ["code"] = p.FailureCode, ["message"] = p.FailureMessage, ["retryable"] = false },
                ["attempts"] = Math.Max(1, p.JobAttempts),
                ["final"] = true,
            });
            return new DeliveryResult("completed", p.FailureCode, [failed]);
        }
        await FinishJobAsync(conn, tx, p.Job.AttemptId, "succeeded", null, ct);
        await SetAuditOutcomeAsync(conn, tx, p.AuditId!.Value, "applied", p.Facts.Count, ct);
        var r = p.Result!;
        var usage = new JsonObject
        {
            ["input_tokens"] = r.InputTokens,
            ["output_tokens"] = r.OutputTokens,
            ["cache_read_tokens"] = r.CacheReadInputTokens,
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

    public void Dispose() => _limiter.Dispose();

    // ---- budget / lease / audit ----

    /// <summary>A rate-limit permit, waited for at most until the deadline; null when the deadline passed first.</summary>
    private async Task<RateLimitLease?> AcquirePermitAsync(DateTimeOffset? deadline, CancellationToken ct)
    {
        if (deadline is null)
        {
            var lease = await _limiter.AcquireAsync(1, ct);
            return lease.IsAcquired ? lease : throw new InvalidOperationException("rate limit permit not granted"); // transient, not a paid attempt
        }
        var remaining = deadline.Value - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(remaining);
        try
        {
            var lease = await _limiter.AcquireAsync(1, timeout.Token);
            return lease.IsAcquired ? lease : throw new InvalidOperationException("rate limit permit not granted");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task RecordLateAsync(NpgsqlConnection conn, Job job, long? auditId, int current, CancellationToken ct)
    {
        await FinishJobAsync(conn, null, job.AttemptId, "superseded", $"late result: token {job.Token}, current {current}", ct);
        if (auditId is long late)
        {
            await SetAuditOutcomeAsync(conn, null, late, "late", null, ct);
        }
        metrics.LlmCall("late");
    }

    private static async Task<bool> HolderRunningAsync(NpgsqlConnection conn, string jobKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT state = 'running' FROM processing.attempts WHERE subscription_id = @s AND job_key = @k ORDER BY fencing_token DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("s", JobSubscription);
        cmd.Parameters.AddWithValue("k", jobKey);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// Takes the lease of a job: under an advisory transaction lock on the key, a live lease of another holder is waited
    /// out (bounded by the lease itself) and then the next fencing token is inserted; an expired `running` row is marked
    /// `interrupted` by the takeover. `Completed` when the holder we waited for succeeded meanwhile (nothing to call);
    /// a null job when the lease is still held after a few rounds.
    /// </summary>
    private async Task<(Job? Job, bool Completed)> AcquireLeaseAsync(NpgsqlConnection conn, string jobKey, Guid requestId, LlmOptions o, CancellationToken ct)
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
                    var state = reader.GetString(1);
                    if (state == "succeeded")
                    {
                        return (null, true);
                    }
                    if (state == "running" && !reader.IsDBNull(2))
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
            await using (var takeover = new NpgsqlCommand(
                "UPDATE processing.attempts SET state = 'interrupted', error = 'lease expired; taken over', finished_at = now(), lease_until = NULL WHERE subscription_id = @s AND job_key = @k AND state = 'running'", conn, tx))
            {
                takeover.Parameters.AddWithValue("s", JobSubscription);
                takeover.Parameters.AddWithValue("k", jobKey);
                await takeover.ExecuteNonQueryAsync(ct);
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
            return (new Job(jobKey, attemptId, maxToken + 1), false);
        }
        return (null, false);
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

    private async Task TryFinishJobAsync(NpgsqlConnection conn, long attemptId, string state, string error)
    {
        try
        {
            await FinishJobAsync(conn, null, attemptId, state, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Job attempt {Attempt} could not be marked {State}; its lease lapses", attemptId, state);
        }
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
        cmd.Parameters.AddWithValue("worker", Producer.Length > WorkerColumnLength ? Producer[..WorkerColumnLength] : Producer);
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

    private static async Task SetAuditOutcomeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long auditId, string outcome, int? factsCount, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE llm_requests SET outcome = @o, facts_count = coalesce(@facts, facts_count) WHERE llm_request_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("o", outcome);
        cmd.Parameters.AddWithValue("facts", (object?)factsCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", auditId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
