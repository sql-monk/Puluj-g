using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Processing.Indexes;

namespace Puluj.Processing.Pipeline;

/// <summary>
/// The single processor instance: <see cref="ProcessingOptions.Concurrency"/> internal workers that claim raw messages
/// from the database (<see cref="RawMessageClaims"/>) and process them, plus a housekeeping task. A PostgreSQL advisory
/// lock prevents a second processor process from running against the same database. A worker first takes an id announced
/// over NOTIFY (RawMessageStored from any collector or the
/// admin panel: a live message goes ahead of a backlog), otherwise the oldest-published Pending row (restart, crash,
/// retry, a reprocess, a history load, a lost notification), so a rebuild replays the situation as it unfolded; with
/// several workers the order holds up to a window of that many messages. While a history load is running
/// (ReprocessService.PausedKey) nothing is claimed, so the load's messages are not processed piecemeal.
/// The housekeeping task re-reads the pause flag and returns claims of dead instances to Pending.
/// </summary>
public sealed class ProcessingLoop(
    IRawMessageQueue queue,
    PgNotifyListener notifications,
    RawMessageProcessor processor,
    RawMessageClaims claims,
    ProcessorSingletonLock singletonLock,
    ProcessorIdentity identity,
    IndexProvider indexes,
    ReprocessService reprocess,
    IOptions<ProcessingOptions> options,
    PulujMetrics metrics,
    ProcessingStats stats,
    ILogger<ProcessingLoop> logger) : BackgroundService
{
    private volatile string? _paused;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using var lease = await singletonLock.AcquireAsync(identity.Name, ct);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Lost);
        try
        {
            await indexes.Ready.WaitAsync(run.Token);
            _paused = await reprocess.PausedAsync(run.Token);
            var concurrency = Math.Max(1, options.Value.Concurrency);
            logger.LogInformation("Processing loop started: instance {Instance}, {Workers} internal worker(s), lease {Lease}", identity.Name, concurrency, options.Value.ClaimLease);
            var tasks = new List<Task> { ListenAsync(run.Token), HousekeepAsync(run.Token) };
            tasks.AddRange(Enumerable.Range(1, concurrency).Select(i => WorkAsync(i, run.Token)));
            await Task.WhenAll(tasks);
            if (lease.Lost.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("The processor singleton database lock was lost; stopping this instance.");
            }
        }
        catch (OperationCanceledException) when (lease.Lost.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The processor singleton database lock was lost; stopping this instance.");
        }
    }

    /// <summary>Queues every raw message announced over NOTIFY. A claim decides who processes it; a duplicate id is harmless.</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in notifications.ListenAsync(ct))
            {
                if (evt.Type == PulujEventType.RawMessageStored)
                {
                    await queue.EnqueueAsync(evt.Id, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task WorkAsync(int worker, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_paused is not null)
                {
                    // Announced ids are dropped: they stay Pending in the database and are taken in order after the pause.
                    while (queue.TryDequeue(out _))
                    {
                    }
                    await IdleAsync(ct);
                    continue;
                }
                long? id = null;
                try
                {
                    if (queue.TryDequeue(out var announced))
                    {
                        id = await claims.ClaimAsync(announced, identity.Name, ct) ? announced : null;
                        if (id is null)
                        {
                            continue; // another instance took it, or it was already processed
                        }
                    }
                    else
                    {
                        id = await claims.ClaimOldestAsync(identity.Name, ct);
                        if (id is null)
                        {
                            await IdleAsync(ct);
                            continue;
                        }
                    }
                    stats.Claimed(id.Value);
                    try
                    {
                        await processor.ProcessAsync(id.Value, ct);
                    }
                    finally
                    {
                        stats.Released(id.Value);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (id is { } claimed)
                    {
                        await ReleaseAsync(claimed);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    // ProcessAsync records its own failures; this is the claim itself (database away): back off a little.
                    logger.LogWarning(ex, "Worker {Worker}: claim failed", worker);
                    if (id is { } claimed)
                    {
                        await ReleaseAsync(claimed);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits for the next announced id or the poll interval, whichever comes first.</summary>
    private async Task IdleAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = queue.WaitToReadAsync(linked.Token).AsTask();
        var tick = Task.Delay(options.Value.PendingPollInterval, linked.Token);
        await Task.WhenAny(signal, tick);
        linked.Cancel();
        try
        {
            await Task.WhenAll(signal, tick);
        }
        catch (OperationCanceledException)
        {
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task ReleaseAsync(long id)
    {
        try
        {
            await claims.ReleaseAsync(id, identity.Name);
            metrics.RawProcessed(identity.Name, "released");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not release RawMessage {Id}; the lease sweep will return it", id);
        }
    }

    private async Task HousekeepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.PendingPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var paused = await reprocess.PausedAsync(ct);
                    if (paused != _paused)
                    {
                        logger.LogInformation(paused is null ? "Processing resumed" : "Processing paused: {Reason}", paused);
                    }
                    _paused = paused;
                    await claims.ReclaimExpiredAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Processing housekeeping failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
