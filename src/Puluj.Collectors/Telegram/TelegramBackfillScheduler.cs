using System.Text.Json.Serialization;
using Puluj.Domain.Entities;
using TL;

namespace Puluj.Collectors.Telegram;

/// <summary>Versioned, resumable state kept under collector_states.cursor.history.</summary>
internal sealed record TelegramHistoryState(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("lastId")] int LastId,
    [property: JsonPropertyName("stored")] long Stored,
    [property: JsonPropertyName("pages")] long Pages,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("floodWaitCount")] int FloodWaitCount,
    [property: JsonPropertyName("lastFailureKind")] string? LastFailureKind)
{
    public static TelegramHistoryState Start(DateTimeOffset since) => new(1, since, 0, 0, 0, false, null, 0, null);
    public TelegramHistoryState Normalize() => Version == 0 ? this with { Version = 1 } : this;
}

internal sealed class TelegramBackfillJob
{
    public required Channel Channel { get; init; }
    public required Source Source { get; init; }
    public required string Username { get; init; }
    public required TelegramHistoryState State { get; set; }
    public bool Running { get; set; }
    public int Weight { get; init; }
}

/// <summary>Weighted fair scheduler: each claim processes one page, then returns the source to the ring.</summary>
internal sealed class TelegramBackfillScheduler(TimeProvider clock, int workers)
{
    private readonly object _sync = new();
    // Each worker keeps one history RPC in flight; the request gate spaces their issue times.
    private readonly int _workers = Math.Clamp(workers, 1, 4);
    private List<TelegramBackfillJob> _ring = [];
    private int _next;

    public async Task RunAsync(IReadOnlyCollection<TelegramBackfillJob> jobs, Func<TelegramBackfillJob, CancellationToken, Task> process, CancellationToken ct)
    {
        lock (_sync)
        {
            // One page from every source comes before the extra weighted slots. This gives a 70-channel import a
            // bounded first-service latency while higher priority sources still receive more pages in each epoch.
            _ring = [];
            var remaining = jobs.ToDictionary(j => j, j => Math.Clamp(j.Weight, 1, 10));
            while (remaining.Any(x => x.Value > 0))
            {
                foreach (var job in jobs)
                {
                    if (remaining[job] > 0)
                    {
                        _ring.Add(job);
                        remaining[job]--;
                    }
                }
            }
            _next = 0;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAll(Enumerable.Range(0, _workers).Select(_ => WorkerAsync(process, stop)));
    }

    private async Task WorkerAsync(Func<TelegramBackfillJob, CancellationToken, Task> process, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var job = ClaimReady();
            if (job is null)
            {
                var delay = DelayUntilReady();
                if (delay is null)
                {
                    return;
                }
                await Task.Delay(delay.Value, ct);
                continue;
            }
            try
            {
                await process(job, ct);
            }
            catch
            {
                // A timeout poisons the session. Stop sibling workers before the exception reaches the
                // collector supervisor, otherwise they could continue to use the same WTelegram client.
                stop.Cancel();
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    job.Running = false;
                }
            }
        }
    }

    private TelegramBackfillJob? ClaimReady()
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            for (var i = 0; i < _ring.Count; i++)
            {
                var job = _ring[_next++ % _ring.Count];
                if (!job.Running && !job.State.Done && (job.State.NextAttemptAt is null || job.State.NextAttemptAt <= now))
                {
                    job.Running = true;
                    return job;
                }
            }
            return null;
        }
    }

    private TimeSpan? DelayUntilReady()
    {
        lock (_sync)
        {
            var remaining = _ring.Where(j => !j.State.Done).Select(j => j.State.NextAttemptAt).Where(x => x is not null).Select(x => x!.Value).ToList();
            if (remaining.Count == 0)
            {
                return _ring.Any(j => !j.State.Done) ? TimeSpan.FromMilliseconds(100) : null;
            }
            return TimeSpan.FromMilliseconds(Math.Clamp((remaining.Min() - clock.GetUtcNow()).TotalMilliseconds, 25, 1000));
        }
    }
}

/// <summary>Serializes history RPC issue time across workers and adapts to Telegram-wide flood pressure.</summary>
internal sealed class TelegramRequestGate
{
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private TimeSpan _interval;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFlood;
    private TelegramRpcTimeoutException? _poisoned;

    public TelegramRequestGate(TimeProvider clock, TimeSpan initialInterval, TimeSpan minimumInterval, TimeSpan maximumInterval)
    {
        if (minimumInterval <= TimeSpan.Zero || maximumInterval < minimumInterval || initialInterval < minimumInterval || initialInterval > maximumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(initialInterval), "Telegram history intervals must be positive and satisfy minimum <= initial <= maximum.");
        }
        _clock = clock;
        _minimum = minimumInterval;
        _maximum = maximumInterval;
        _interval = initialInterval;
    }

    public async Task WaitTurnAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            if (_lastFlood is { } lastFlood && now - lastFlood >= TimeSpan.FromMinutes(30) && _interval > _minimum)
            {
                _interval = TimeSpan.FromMilliseconds(Math.Max(_minimum.TotalMilliseconds, _interval.TotalMilliseconds * 0.75));
                _lastFlood = now;
            }
            var at = new[] { now, _next, _blockedUntil }.Max();
            _next = at + _interval;
            var delay = at - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task FloodAsync(int seconds, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            ApplyFlood(seconds);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Paces the *issue* of an RPC (one every interval, none during a flood cooldown) but does not hold the coordinator
    /// while the RPC is in flight: a history page takes ~1.5 s to come back from Telegram, so serialising the calls
    /// would cap the whole import at one page per RPC round trip regardless of how many workers run. Up to one RPC per
    /// worker overlaps; a received flood or timeout is applied when it arrives and stops every later issue.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> rpc, TimeSpan timeout, string operation, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        Task<T>? pending;
        try
        {
            if (_poisoned is not null)
            {
                throw new TelegramSessionRestartRequiredException(operation, _poisoned);
            }
            var now = _clock.GetUtcNow();
            if (_lastFlood is { } lastFlood && now - lastFlood >= TimeSpan.FromMinutes(30) && _interval > _minimum)
            {
                _interval = TimeSpan.FromMilliseconds(Math.Max(_minimum.TotalMilliseconds, _interval.TotalMilliseconds * 0.75));
                _lastFlood = now;
            }
            var at = new[] { now, _next, _blockedUntil }.Max();
            _next = at + _interval;
            if (at > now)
            {
                await Task.Delay(at - now, ct);
            }
            try
            {
                pending = rpc();
            }
            catch (Exception ex)
            {
                pending = Task.FromException<T>(ex);
            }
        }
        finally
        {
            _mutex.Release();
        }
        try
        {
            return await pending.WaitAsync(timeout, ct);
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            await FloodAsync(ex.X, ct);
            throw;
        }
        catch (TimeoutException ex) when (!ct.IsCancellationRequested)
        {
            var timeoutException = new TelegramRpcTimeoutException(operation, ex);
            _ = ObserveLateFailureAsync(pending);
            await _mutex.WaitAsync(CancellationToken.None);
            try
            {
                _poisoned ??= timeoutException;
                ApplyFlood((int)Math.Ceiling(timeout.TotalSeconds));
            }
            finally
            {
                _mutex.Release();
            }
            throw timeoutException;
        }
    }

    private void ApplyFlood(int seconds)
    {
        var now = _clock.GetUtcNow();
        _lastFlood = now;
        _interval = TimeSpan.FromMilliseconds(Math.Min(_maximum.TotalMilliseconds, Math.Max(_interval.TotalMilliseconds * 2, _minimum.TotalMilliseconds)));
        _blockedUntil = new[] { _blockedUntil, now.AddSeconds(Math.Max(1, seconds) + 1) }.Max();
    }

    private static async Task ObserveLateFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The timed-out session will be disposed; observe the abandoned RPC so it cannot become unobserved.
        }
    }
}

internal sealed class TelegramRpcTimeoutException(string operation, Exception inner) : TimeoutException($"Telegram {operation} exceeded the configured RPC timeout.", inner);
internal sealed class TelegramSessionRestartRequiredException(string operation, Exception inner) : InvalidOperationException($"Telegram {operation} cannot start because the MTProto session is restarting.", inner);

/// <summary>Small testable boundary around a non-cancellable MTProto RPC. Its timeout causes the outer session to restart.</summary>
internal sealed class TelegramRpcExecutor(TelegramRequestGate gate, TimeSpan timeout)
{
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> rpc, string operation, CancellationToken ct)
    {
        return await gate.ExecuteAsync(rpc, timeout, operation, ct);
    }
}
