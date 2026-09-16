using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Puluj.Domain.Entities.Messaging;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Puluj.Messaging;

/// <summary>
/// One replica of one subscription (ADR-0004 §6.2, ADR-0001 §8): a channel with manual ACK per lane of the subscription.
/// Per delivery: parse and schema check → inbox fast path → attempt row → handler work outside the transaction →
/// short transaction (inbox insert with re-check, effect, receipt, outgoing outbox rows, attempt succeeded) → commit →
/// ACK. Transient failures wait a bounded backoff while the delivery stays unacked and are then requeued with
/// `basic.nack` (the broker's delivery-limit only counts crashes); when `processing.attempts` reaches the policy limit,
/// or the input is invalid, the delivery is committed as `quarantined` first and only then dead-lettered.
/// </summary>
public sealed class SubscriptionConsumer : BackgroundService
{
    private readonly IDeliveryHandler _handler;
    private readonly BrokerConnection _broker;
    private readonly TopologyRegistrar _registrar;
    private readonly IDbContextFactory<PulujDbContext> _factory;
    private readonly OutboxWriter _outbox;
    private readonly MessagingOptions _options;
    private readonly MessagingMetrics _metrics;
    private readonly ILogger _logger;
    private readonly string _worker;
    /// <summary>One runtime per lane of the subscription (P13 controls): the channel, the consumer tag, the last state read from `messaging.subscription_lanes` and the in-flight count. Guarded by its own lock.</summary>
    private readonly Dictionary<string, LaneRuntime> _lanes = new(StringComparer.Ordinal);
    private TaskCompletionSource _crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _controlTableMissingLogged;

    public SubscriptionConsumer(
        IDeliveryHandler handler,
        BrokerConnection broker,
        TopologyRegistrar registrar,
        IDbContextFactory<PulujDbContext> factory,
        OutboxWriter outbox,
        IOptions<MessagingOptions> options,
        MessagingMetrics metrics,
        ILoggerFactory loggerFactory,
        string? worker = null)
    {
        _handler = handler;
        _broker = broker;
        _registrar = registrar;
        _factory = factory;
        _outbox = outbox;
        _options = options.Value;
        _metrics = metrics;
        _logger = loggerFactory.CreateLogger($"Puluj.Messaging.Consumer.{handler.SubscriptionId}");
        _worker = worker ?? $"{handler.SubscriptionId}@{Environment.MachineName.ToLowerInvariant()}";
    }

    public string SubscriptionId => _handler.SubscriptionId;
    public IDeliveryHandler Handler => _handler;
    public TopologyRegistry Registry => _registrar.Registry;
    /// <summary>Crash points (tests); replaced per test.</summary>
    public ConsumerHooks Hooks { get; set; } = ConsumerHooks.None;
    /// <summary>Overrides the policy limit (tests); null = `queue_policies.max_delivery_attempts`.</summary>
    public int? MaxAttemptsOverride { get; set; }
    /// <summary>Completes when a simulated crash closed the channels; the service re-consumes after <see cref="RestartDelay"/>.</summary>
    public Task Crashed => _crashed.Task;
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>What one lane of this replica is doing right now (worker status `Consumers[]`, P13).</summary>
    public sealed record LaneStatus(string Lane, string Queue, string State, bool Consuming, int InFlight, ushort Prefetch, string ConsumerTag);

    /// <summary>Snapshot of every lane runtime of this replica (only lanes this process serves).</summary>
    public IReadOnlyList<LaneStatus> Lanes()
    {
        lock (_lanes)
        {
            return _lanes.Values.Select(l => new LaneStatus(l.Lane, l.Queue, l.State, l.Consuming, l.InFlight, l.Prefetch, l.Tag)).ToList();
        }
    }

    private sealed class LaneRuntime(string lane, string queue, ushort prefetch, string tag)
    {
        public string Lane { get; } = lane;
        public string Queue { get; } = queue;
        public ushort Prefetch { get; } = prefetch;
        public string Tag { get; } = tag;
        public IChannel? Channel { get; set; }
        public bool Consuming { get; set; }
        /// <summary>Deliveries received and not yet acked/nacked on this replica (own view only: other replicas count their own).</summary>
        public int InFlight;
        public string State { get; set; } = SubscriptionLane.Active;
        /// <summary>Consecutive polls of a draining lane that saw an empty queue and no in-flight delivery.</summary>
        public int DrainedPolls { get; set; }
    }

    private long _delivered;
    private long _duplicates;
    private long _requeued;

    /// <summary>Deliveries received by this replica (evidence counters for tests and status).</summary>
    public long Delivered => Volatile.Read(ref _delivered);
    public long Duplicates => Volatile.Read(ref _duplicates);
    public long Requeued => Volatile.Read(ref _requeued);

    /// <summary>Zeroes the evidence counters (tests share one instance across cases).</summary>
    public void ResetCounters()
    {
        Volatile.Write(ref _delivered, 0);
        Volatile.Write(ref _duplicates, 0);
        Volatile.Write(ref _requeued, 0);
    }

    private int MaxAttempts => MaxAttemptsOverride ?? Registry.Policy(Registry.Subscription(SubscriptionId)).MaxDeliveryAttempts;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _registrar.EnsureRegisteredAsync(ct);
                await ConsumeAsync(ct);
                // Control poll (P13, ADR-0012) in the same loop as the crash wait: no second timer races Crash()/shutdown.
                while (!ct.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(Crashed, Task.Delay(_options.Consumer.ControlPoll, ct));
                    if (ct.IsCancellationRequested || completed == Crashed)
                    {
                        break;
                    }
                    await PollControlAsync(ct);
                }
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                _logger.LogWarning("Consumer {Subscription} crashed (simulated); restarting in {Delay}", SubscriptionId, RestartDelay);
                _crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.Delay(RestartDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer {Subscription} failed to start; retrying in 5 s", SubscriptionId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        await CloseChannelsAsync();
    }

    /// <summary>
    /// Opens the lanes this process serves. The lane states are read before the first `basic.consume`, so a paused lane
    /// never takes `prefetch` deliveries after a restart; a lane whose channel is still open (a poll failure re-entered
    /// the start path) is left alone.
    /// </summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        var subscription = Registry.Subscription(SubscriptionId);
        var lanes = _options.Consumer.Lanes.Length == 0 ? subscription.Lanes : subscription.Lanes.Where(l => _options.Consumer.Lanes.Contains(l, StringComparer.Ordinal)).ToList();
        var prefetch = _options.Consumer.PrefetchBySubscription.TryGetValue(SubscriptionId, out var own) ? own : _options.Consumer.Prefetch;
        var states = await LaneStatesAsync(ct);
        var connection = await _broker.GetAsync(ct);
        var consuming = new List<string>();
        var paused = new List<string>();
        foreach (var lane in lanes)
        {
            LaneRuntime runtime;
            lock (_lanes)
            {
                if (!_lanes.TryGetValue(lane, out runtime!))
                {
                    runtime = new LaneRuntime(lane, Registry.QueueName(SubscriptionId, lane), prefetch, $"{_worker}:{lane}");
                    _lanes[lane] = runtime;
                }
                runtime.State = states.GetValueOrDefault(lane, SubscriptionLane.Active);
            }
            if (runtime.State == SubscriptionLane.Paused)
            {
                paused.Add(lane);
                continue;
            }
            await StartLaneAsync(runtime, connection, ct);
            consuming.Add(lane);
        }
        _logger.LogInformation("Consumer {Subscription} ({Worker}) consuming lanes {Lanes}, prefetch {Prefetch}{Paused}", SubscriptionId, _worker, string.Join(",", consuming), prefetch,
            paused.Count == 0 ? "" : $"; paused by operator: {string.Join(",", paused)}");
    }

    private async Task StartLaneAsync(LaneRuntime runtime, IConnection connection, CancellationToken ct)
    {
        IChannel? channel;
        lock (_lanes)
        {
            if (runtime.Consuming && runtime.Channel is { IsOpen: true })
            {
                return;
            }
            runtime.Consuming = false; // a consuming lane whose channel closed underneath (server-side channel error) is re-opened here
            channel = runtime.Channel;
        }
        if (channel is not { IsOpen: true })
        {
            channel = await connection.CreateChannelAsync(cancellationToken: ct);
            await channel.BasicQosAsync(0, runtime.Prefetch, false, ct);
        }
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => OnReceivedAsync(runtime, channel, ea, ct);
        await channel.BasicConsumeAsync(runtime.Queue, autoAck: false, consumerTag: runtime.Tag, consumer, cancellationToken: ct);
        lock (_lanes)
        {
            // Crash()/shutdown may have taken the channels meanwhile: then this consume is on a closing channel and the restart re-reads the state.
            runtime.Channel = channel;
            runtime.Consuming = channel.IsOpen;
        }
    }

    /// <summary>`basic.cancel` of the lane's consumer tag: no new deliveries; the channel stays open so in-flight deliveries are still acked after their commit.</summary>
    private async Task StopLaneAsync(LaneRuntime runtime, CancellationToken ct)
    {
        IChannel? channel;
        lock (_lanes)
        {
            if (!runtime.Consuming)
            {
                return;
            }
            runtime.Consuming = false;
            channel = runtime.Channel;
        }
        if (channel is { IsOpen: true })
        {
            await channel.BasicCancelAsync(runtime.Tag, noWait: false, ct);
        }
    }

    /// <summary>Lane states of this subscription from `messaging.subscription_lanes`; a missing row (or a database older than the P13 migration) means `active`.</summary>
    private async Task<Dictionary<string, string>> LaneStatesAsync(CancellationToken ct)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT lane, state FROM messaging.subscription_lanes WHERE subscription_id = @s", conn);
            cmd.Parameters.AddWithValue("s", SubscriptionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                states[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            if (!_controlTableMissingLogged)
            {
                _controlTableMissingLogged = true;
                _logger.LogWarning("messaging.subscription_lanes does not exist (database older than migration AddMessagingControls): every lane is treated as active");
            }
        }
        return states;
    }

    /// <summary>
    /// One control tick (ADR-0012): `paused` cancels the lane's consumer, `active` consumes again, `draining` keeps consuming
    /// until the queue is empty and nothing is in flight on two consecutive ticks, then this replica sets the lane `paused`
    /// itself (actor `system`, audit `drained`). A failed tick is logged and retried on the next one; it never stops the consumer.
    /// </summary>
    private async Task PollControlAsync(CancellationToken ct)
    {
        try
        {
            var states = await LaneStatesAsync(ct);
            List<LaneRuntime> lanes;
            lock (_lanes)
            {
                lanes = [.. _lanes.Values];
            }
            foreach (var runtime in lanes)
            {
                var state = states.GetValueOrDefault(runtime.Lane, SubscriptionLane.Active);
                bool changed;
                lock (_lanes)
                {
                    changed = runtime.State != state;
                    runtime.State = state;
                    if (changed)
                    {
                        runtime.DrainedPolls = 0;
                    }
                }
                if (changed)
                {
                    _logger.LogInformation("Consumer {Subscription} lane {Lane}: operator state {State}", SubscriptionId, runtime.Lane, state);
                }
                switch (state)
                {
                    case SubscriptionLane.Paused:
                        await StopLaneAsync(runtime, ct);
                        break;
                    case SubscriptionLane.Active:
                        await StartLaneAsync(runtime, await _broker.GetAsync(ct), ct);
                        break;
                    case SubscriptionLane.Draining:
                        await StartLaneAsync(runtime, await _broker.GetAsync(ct), ct);
                        await DrainTickAsync(runtime, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consumer {Subscription}: control poll failed; retrying on the next tick", SubscriptionId);
        }
    }

    private async Task DrainTickAsync(LaneRuntime runtime, CancellationToken ct)
    {
        uint ready;
        var connection = await _broker.GetAsync(ct);
        await using (var probe = await connection.CreateChannelAsync(cancellationToken: ct)) // a passive declare that fails closes its channel: never the consuming one
        {
            ready = (await probe.QueueDeclarePassiveAsync(runtime.Queue, ct)).MessageCount;
        }
        int polls;
        lock (_lanes)
        {
            runtime.DrainedPolls = ready == 0 && Volatile.Read(ref runtime.InFlight) == 0 ? runtime.DrainedPolls + 1 : 0;
            polls = runtime.DrainedPolls;
        }
        if (polls < 2)
        {
            return;
        }
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int updated;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE messaging.subscription_lanes SET state = 'paused', actor = 'system', reason = 'drained', changed_at = now() WHERE subscription_id = @s AND lane = @l AND state = 'draining'", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", SubscriptionId);
            cmd.Parameters.AddWithValue("l", runtime.Lane);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (updated == 1) // an operator may have resumed the lane between the read and here: then the audit row is theirs, not ours
        {
            await using var audit = new NpgsqlCommand(
                "INSERT INTO messaging.control_audit (action, subscription_id, lane, actor, reason, at, details) VALUES ('drained', @s, @l, 'system', 'queue empty, nothing in flight', now(), @d)", conn, tx);
            audit.Parameters.AddWithValue("s", SubscriptionId);
            audit.Parameters.AddWithValue("l", runtime.Lane);
            audit.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { worker = _worker, queue = runtime.Queue }) });
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (updated == 1)
        {
            lock (_lanes)
            {
                runtime.State = SubscriptionLane.Paused;
                runtime.DrainedPolls = 0;
            }
            _logger.LogInformation("Consumer {Subscription} lane {Lane} drained: paused by system", SubscriptionId, runtime.Lane);
            await StopLaneAsync(runtime, ct);
        }
    }

    private async Task OnReceivedAsync(LaneRuntime runtime, IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        Interlocked.Increment(ref runtime.InFlight);
        try
        {
            await OnReceivedAsync(channel, ea, ct);
        }
        finally
        {
            Interlocked.Decrement(ref runtime.InFlight);
        }
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        Interlocked.Increment(ref _delivered);
        var body = ea.Body.ToArray();
        var envelope = Envelope.TryParse(body, out var parseError);
        var eventId = envelope?.EventId ?? (Guid.TryParse(ea.BasicProperties.MessageId, out var fromProps) ? fromProps : Guid.Empty);
        try
        {
            if (envelope is null)
            {
                await QuarantineAsync(channel, ea, eventId, null, body, "invalid_payload", parseError, null, ct);
                return;
            }
            if (!Registry.Events.TryGetValue(envelope.EventType, out var definition))
            {
                await QuarantineAsync(channel, ea, eventId, envelope, body, "unknown_event", $"event type {envelope.EventType} is not in topology.json v{Registry.TopologyVersion}", null, ct);
                return;
            }
            if (TopologyRegistry.ParseMajor(envelope.SchemaVersion) != definition.SchemaMajor)
            {
                await QuarantineAsync(channel, ea, eventId, envelope, body, "incompatible_schema", $"schema_version {envelope.SchemaVersion} vs supported {definition.SchemaVersion}", null, ct);
                return;
            }

            await using var db = await _factory.CreateDbContextAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);

            // 1. Inbox fast path (W3/W5/W7b/W14a): already terminal → ACK (or repeat the DLQ transfer) without any effect.
            var known = await InboxOutcomeAsync(conn, eventId, ct);
            if (known is not null)
            {
                Interlocked.Increment(ref _duplicates);
                _metrics.Duplicate(SubscriptionId);
                if (known == "quarantined")
                {
                    await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct); // W6a-2: idempotent transfer
                }
                else
                {
                    await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                }
                return;
            }

            // 2. Attempts so far (W6a-1): a redelivery after the limit gets no new attempt, only the quarantine receipt.
            var attemptsSoFar = await CountAttemptsAsync(conn, eventId, ct);
            if (attemptsSoFar >= MaxAttempts)
            {
                await QuarantineAsync(channel, ea, eventId, envelope, body, "attempts_exhausted", $"{attemptsSoFar} attempts (limit {MaxAttempts})", null, ct);
                return;
            }
            var attemptId = await StartAttemptAsync(conn, eventId, ct);

            try
            {
                // 3. Work outside the transaction, then the short transaction with re-check.
                var state = await _handler.PrepareAsync(envelope, ct);
                Hooks.BeforeCommit(envelope);
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    if (!await InsertInboxAsync(conn, tx, eventId, ct))
                    {
                        // Another replica committed the same delivery between the fast path and here.
                        await tx.RollbackAsync(ct);
                        await FinishAttemptAsync(conn, attemptId, "superseded", null, ct);
                        Interlocked.Increment(ref _duplicates);
                        _metrics.Duplicate(SubscriptionId);
                        await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                        return;
                    }
                    var result = await _handler.ApplyAsync(conn, tx, envelope, state, ct);
                    await CompleteInboxAsync(conn, tx, eventId, result.Outcome, ct);
                    await WriteReceiptAsync(conn, tx, eventId, envelope.TopologyVersion, result.Outcome, result.Reason, null, attemptId, ct, envelope);
                    foreach (var outgoing in result.OutEvents ?? [])
                    {
                        await _outbox.EnqueueAsync(conn, tx, outgoing, ct);
                    }
                    await FinishAttemptAsync(conn, tx, attemptId, "succeeded", null, ct, result.StageResultId);
                    await tx.CommitAsync(ct);
                    _metrics.Delivered(SubscriptionId, result.Outcome);
                    if (result.AfterCommit is not null)
                    {
                        try
                        {
                            await result.AfterCommit(ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Post-commit side effect of {EventId} failed; the result is committed, delivery is acknowledged", eventId);
                        }
                    }
                }
                Hooks.AfterCommitBeforeAck(envelope);
                await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
            }
            catch (SimulatedCrashException ex)
            {
                _logger.LogWarning("Simulated crash at {Point} for {EventId}", ex.Point, eventId);
                Crash();
            }
            catch (PermanentDeliveryException ex)
            {
                await FinishAttemptAsync(conn, attemptId, "failed", ex.Message, ct);
                await QuarantineAsync(channel, ea, eventId, envelope, body, ex.Reason, ex.Message, attemptId, ct);
            }
            catch (Exception ex) when (ex is DeliveryDeferredException || IsSerializationFailure(ex))
            {
                // Not a failure: the outcome belongs to another delivery of the same command, or the database asked us to
                // retry (deadlock / serialization failure between two writers, P09 §7) — requeue without counting.
                await FinishAttemptAsync(conn, attemptId, "superseded", ex.Message, ct);
                _logger.LogInformation("Delivery {EventId} to {Subscription} deferred: {Reason}", eventId, SubscriptionId, ex.Message);
                _metrics.Delivered(SubscriptionId, "deferred");
                await Task.Delay(_options.Consumer.MinBackoff, ct);
                Interlocked.Increment(ref _requeued);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FinishAttemptAsync(conn, attemptId, "failed", ex.ToString(), ct);
                var attempts = attemptsSoFar + 1;
                if (attempts >= MaxAttempts)
                {
                    _logger.LogError(ex, "Delivery {EventId} to {Subscription} failed {Attempts} times: quarantine", eventId, SubscriptionId, attempts);
                    await QuarantineAsync(channel, ea, eventId, envelope, body, "attempts_exhausted", ex.Message, attemptId, ct);
                    return;
                }
                var delay = Backoff(attempts);
                _logger.LogWarning(ex, "Delivery {EventId} to {Subscription} failed (attempt {Attempt}/{Max}); requeue in {Delay}", eventId, SubscriptionId, attempts, MaxAttempts, delay);
                _metrics.Delivered(SubscriptionId, "retry");
                await Task.Delay(delay, ct);
                Interlocked.Increment(ref _requeued);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
            }
        }
        catch (SimulatedCrashException ex)
        {
            _logger.LogWarning("Simulated crash at {Point} for {EventId}", ex.Point, eventId);
            Crash();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown: the unacked delivery returns to the queue
        }
        catch (Exception ex)
        {
            // Database unreachable before any attempt could be recorded: leave the delivery unacked, the broker redelivers.
            _logger.LogError(ex, "Delivery {EventId} to {Subscription}: infrastructure failure before processing; requeue", eventId, SubscriptionId);
            try
            {
                await Task.Delay(_options.Consumer.MinBackoff, ct);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
            }
            catch (Exception nackError)
            {
                _logger.LogDebug(nackError, "Requeue after infrastructure failure did not go through; the broker will redeliver on channel close");
            }
        }
    }

    /// <summary>
    /// W6: durable quarantine (inbox + receipt + quarantine row in one transaction) before the DLQ transfer, so a crash
    /// between them only repeats an idempotent nack. Reason and error stay with the envelope for the operator.
    /// </summary>
    private async Task QuarantineAsync(IChannel channel, BasicDeliverEventArgs ea, Guid eventId, Envelope? envelope, byte[] body, string reason, string? error, long? attemptId, CancellationToken ct)
    {
        if (eventId == Guid.Empty)
        {
            eventId = BodyEventId(body); // body without an id: a stable key, so a repeated crash/redelivery does not open a second quarantine
        }
        Hooks.BeforeQuarantineCommit(envelope);
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await UpsertInboxAsync(conn, tx, eventId, "quarantined", ct);
            await WriteReceiptAsync(conn, tx, eventId, envelope?.TopologyVersion ?? Registry.TopologyVersion, "quarantined", reason, null, attemptId, ct, envelope);
            await InsertQuarantineAsync(conn, tx, eventId, envelope?.Lane ?? LaneFromHeaders(ea), reason, error, envelope is null ? RawBodyAsJson(body) : envelope.ToArchiveJson(), HeadersAsJson(ea), attemptId, ct);
            await tx.CommitAsync(ct);
        }
        _metrics.Quarantined(SubscriptionId, reason);
        _metrics.Delivered(SubscriptionId, "quarantined");
        Hooks.AfterQuarantineCommitBeforeNack(envelope);
        await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
    }

    private TimeSpan Backoff(int attempts)
    {
        var c = _options.Consumer;
        var delay = Math.Min(c.MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20)), c.MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delay + delay * Random.Shared.NextDouble() * 0.25);
    }

    /// <summary>Closing the channel from inside its own dispatch loop would wait for this callback: close from another task.</summary>
    /// <summary>PostgreSQL 40001 (serialization_failure) / 40P01 (deadlock_detected): the transaction was rolled back by the server; the delivery is simply tried again.</summary>
    private static bool IsSerializationFailure(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: "40001" or "40P01" };

    /// <summary>Simulated crash (tests): the channels close without an ACK/NACK, the broker redelivers, the service re-consumes after <see cref="RestartDelay"/>.</summary>
    private void Crash()
    {
        var channels = TakeChannels();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var channel in channels)
                {
                    try
                    {
                        await channel.CloseAsync();
                        await channel.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Closing channel after simulated crash");
                    }
                }
            }
            finally
            {
                _crashed.TrySetResult();
            }
        });
    }

    /// <summary>Detaches every lane's channel (crash or shutdown); the lane runtimes stay, so a restart reads the operator state again before consuming.</summary>
    private List<IChannel> TakeChannels()
    {
        var channels = new List<IChannel>();
        lock (_lanes)
        {
            foreach (var runtime in _lanes.Values)
            {
                if (runtime.Channel is { } channel)
                {
                    channels.Add(channel);
                }
                runtime.Channel = null;
                runtime.Consuming = false;
                runtime.DrainedPolls = 0;
            }
        }
        return channels;
    }

    private async Task CloseChannelsAsync()
    {
        var channels = TakeChannels();
        foreach (var channel in channels)
        {
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync();
                }
                await channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing consumer channel");
            }
        }
    }

    // ---- SQL ----

    private async Task<string?> InboxOutcomeAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT outcome FROM messaging.inbox WHERE subscription_id = @s AND event_id = @e", conn);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private async Task<bool> InsertInboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO messaging.inbox (subscription_id, event_id, received_at, outcome) VALUES (@s, @e, now(), 'processing') ON CONFLICT DO NOTHING RETURNING event_id", conn, tx);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task CompleteInboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, string outcome, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE messaging.inbox SET outcome = @o, completed_at = now() WHERE subscription_id = @s AND event_id = @e", conn, tx);
        cmd.Parameters.AddWithValue("o", outcome);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertInboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, string outcome, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO messaging.inbox (subscription_id, event_id, received_at, completed_at, outcome) VALUES (@s, @e, now(), now(), @o)
            ON CONFLICT (subscription_id, event_id) DO UPDATE SET outcome = EXCLUDED.outcome, completed_at = EXCLUDED.completed_at
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("o", outcome);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Terminal receipt; an expected row is completed, a missing one (optional/unregistered subscription) is created. `completed`/`noop` are never downgraded; `quarantined` (retry) and `waived` (backlog processed after all) may become `completed`.</summary>
    internal static async Task WriteReceiptAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string subscriptionId, Guid eventId, int topologyVersion, string outcome, string? reason, string? actor, long? attemptId, CancellationToken ct, string? lane = null, DateTimeOffset? occurredAt = null)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.deliveries (event_id, subscription_id, topology_version, expected_at, outcome, completed_at, reason, actor, attempt_id, lane, occurred_at)
            VALUES (@e, @s, @v, now(), @o, now(), @r, @a, @attempt, @lane, @occurred)
            ON CONFLICT (event_id, subscription_id) DO UPDATE
                SET outcome = EXCLUDED.outcome, completed_at = EXCLUDED.completed_at, reason = EXCLUDED.reason, actor = EXCLUDED.actor, attempt_id = EXCLUDED.attempt_id,
                    lane = COALESCE(processing.deliveries.lane, EXCLUDED.lane), occurred_at = COALESCE(processing.deliveries.occurred_at, EXCLUDED.occurred_at)
                WHERE processing.deliveries.outcome IS NULL OR processing.deliveries.outcome IN ('quarantined', 'waived')
            """, conn, tx);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("s", subscriptionId);
        cmd.Parameters.AddWithValue("v", topologyVersion);
        cmd.Parameters.AddWithValue("o", outcome);
        cmd.Parameters.AddWithValue("r", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a", (object?)actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("attempt", (object?)attemptId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lane", (object?)lane ?? DBNull.Value);
        cmd.Parameters.AddWithValue("occurred", (object?)occurredAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Task WriteReceiptAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, int topologyVersion, string outcome, string? reason, string? actor, long? attemptId, CancellationToken ct, Envelope? envelope = null) =>
        WriteReceiptAsync(conn, tx, SubscriptionId, eventId, topologyVersion, outcome, reason, actor, attemptId, ct, envelope?.Lane, envelope?.OccurredAt);

    private async Task<int> CountAttemptsAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM processing.attempts WHERE subscription_id = @s AND event_id = @e AND state <> 'succeeded' AND state <> 'superseded'", conn);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Records the attempt before the work (autocommit): a crash leaves it `running`, which the next delivery marks `interrupted` and counts. After an admin retry the new attempt links to the last superseded one (W6b).</summary>
    private async Task<long> StartAttemptAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct)
    {
        // A `running` attempt of a crashed process; a live one on another replica (duplicate copy in flight) is younger than the
        // stale window and is left alone — the inbox re-check decides which of the two commits.
        await using (var interrupt = new NpgsqlCommand("UPDATE processing.attempts SET state = 'interrupted', finished_at = now() WHERE subscription_id = @s AND event_id = @e AND state = 'running' AND started_at < now() - @stale", conn))
        {
            interrupt.Parameters.AddWithValue("s", SubscriptionId);
            interrupt.Parameters.AddWithValue("e", eventId);
            interrupt.Parameters.AddWithValue("stale", _options.Consumer.StaleAttempt);
            await interrupt.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.attempts (subscription_id, event_id, job_key, worker, state, fencing_token, started_at, retry_of_attempt_id, retry_reason)
            SELECT @s, @e, @job, @worker, 'running', 0, now(), prev.attempt_id, CASE WHEN prev.attempt_id IS NULL THEN NULL ELSE 'admin_retry' END
            FROM (SELECT max(attempt_id) AS attempt_id FROM processing.attempts WHERE subscription_id = @s AND event_id = @e AND state = 'superseded') prev
            RETURNING attempt_id
            """, conn);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("job", $"{SubscriptionId}:{eventId}");
        cmd.Parameters.AddWithValue("worker", _worker);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task FinishAttemptAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long attemptId, string state, string? error, CancellationToken ct, long? stageResultId = null)
    {
        await using var cmd = new NpgsqlCommand("UPDATE processing.attempts SET state = @state, error = @error, finished_at = now(), stage_result_id = COALESCE(@stage, stage_result_id) WHERE attempt_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("state", state);
        cmd.Parameters.AddWithValue("error", (object?)(error is { Length: > 4000 } ? error[..4000] : error) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stage", (object?)stageResultId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", attemptId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task FinishAttemptAsync(NpgsqlConnection conn, long attemptId, string state, string? error, CancellationToken ct) =>
        FinishAttemptAsync(conn, null, attemptId, state, error, ct);

    private async Task InsertQuarantineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid eventId, string lane, string reason, string? error, string envelopeJson, string? headersJson, long? attemptId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.quarantine (subscription_id, event_id, lane, reason, error, envelope, headers, last_attempt_id, quarantined_at)
            VALUES (@s, @e, @lane, @reason, @error, @envelope, @headers, @attempt, now())
            ON CONFLICT (subscription_id, event_id) WHERE resolved_at IS NULL DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", SubscriptionId);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("lane", lane);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("error", (object?)(error is { Length: > 4000 } ? error[..4000] : error) ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = envelopeJson });
        cmd.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = (object?)headersJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("attempt", (object?)attemptId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deterministic id for a body that carries none (UUIDv5 over the body hash): the same garbage always maps to the same quarantine row.</summary>
    internal static Guid BodyEventId(byte[] body) =>
        SourceIdentity.NameBasedGuid(new Guid("3b6f0c3e-5a0d-4c1e-9d7a-2f1e8d9c0b11"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)));

    internal static string RawBodyAsJson(byte[] body) =>
        JsonSerializer.Serialize(new { invalid_body = Convert.ToBase64String(body.Length > 65536 ? body[..65536] : body) });

    internal static string? HeadersAsJson(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers is not { Count: > 0 } headers)
        {
            return null;
        }
        var plain = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            plain[key] = value switch
            {
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                List<object?> list => list.Select(v => v is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : v?.ToString()).ToList(),
                _ => value?.ToString(),
            };
        }
        return JsonSerializer.Serialize(plain);
    }

    private static string LaneFromHeaders(BasicDeliverEventArgs ea) =>
        ea.BasicProperties.Headers is { } h && h.TryGetValue("lane", out var lane) && lane is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "live";
}
