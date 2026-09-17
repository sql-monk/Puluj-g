using Puluj.Collectors.Telegram;
using Puluj.Domain.Entities;
using TL;

namespace Puluj.Collectors.Tests;

public class TelegramBackfillSchedulerTests
{
    [Fact]
    public void Legacy_cursor_is_upgraded_without_losing_progress()
    {
        var legacy = new TelegramHistoryState(0, new DateTimeOffset(2022, 2, 24, 0, 0, 0, TimeSpan.Zero), 42, 99, 0, false, null, 0, null);

        var current = legacy.Normalize();

        Assert.Equal(1, current.Version);
        Assert.Equal(42, current.LastId);
        Assert.Equal(99, current.Stored);
    }

    [Fact]
    public async Task Weighted_ring_eventually_runs_low_priority_source()
    {
        var high = Job(1, 10);
        var low = Job(2, 1);
        var order = new List<int>();
        var highPages = 0;
        var scheduler = new TelegramBackfillScheduler(TimeProvider.System, 1);

        await scheduler.RunAsync([high, low], (job, _) =>
        {
            order.Add(job.Source.SourceId);
            if (job.Source.SourceId == high.Source.SourceId && ++highPages < 10)
            {
                return Task.CompletedTask;
            }
            job.State = job.State with { Done = true };
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(11, order.Count);
        Assert.Contains(low.Source.SourceId, order);
    }

    [Fact]
    public async Task Scheduler_never_assigns_one_source_to_two_workers()
    {
        var job = Job(1, 10);
        var active = 0;
        var maximum = 0;
        var scheduler = new TelegramBackfillScheduler(TimeProvider.System, 2);

        await scheduler.RunAsync([job], async (current, _) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            current.State = current.State with { Done = true };
        }, CancellationToken.None);

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Seventy_channels_each_receive_a_page_before_weighted_extra_slots()
    {
        var jobs = Enumerable.Range(1, 70).Select(id => Job(id, id == 1 ? 10 : 1)).ToList();
        var order = new List<int>();
        var scheduler = new TelegramBackfillScheduler(TimeProvider.System, 1);

        await scheduler.RunAsync(jobs, (job, _) =>
        {
            order.Add(job.Source.SourceId);
            job.State = job.State with { Done = true };
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(70, order.Count);
        Assert.Equal(70, order.Distinct().Count());
        Assert.True(order.IndexOf(70) < 70);
    }

    [Fact]
    public async Task Rpc_timeout_is_reported_before_a_new_session_can_continue()
    {
        var gate = new TelegramRequestGate(TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var executor = new TelegramRpcExecutor(gate, TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<TelegramRpcTimeoutException>(() => executor.ExecuteAsync(async () =>
        {
            await Task.Delay(100);
            return 1;
        }, "test", CancellationToken.None));
    }

    [Fact]
    public async Task Request_gate_never_starts_two_history_rpcs_together()
    {
        var gate = new TelegramRequestGate(TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var executor = new TelegramRpcExecutor(gate, TimeSpan.FromSeconds(1));
        var active = 0;
        var maximum = 0;

        async Task<int> RpcAsync()
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            return 1;
        }

        await Task.WhenAll(
            executor.ExecuteAsync(RpcAsync, "one", CancellationToken.None),
            executor.ExecuteAsync(RpcAsync, "two", CancellationToken.None));

        Assert.Equal(1, maximum);
    }

    private static TelegramBackfillJob Job(int sourceId, int weight) => new()
    {
        Channel = new Channel(),
        Source = new Source { SourceId = sourceId, Code = $"tg-{sourceId}", Name = $"Telegram {sourceId}" },
        Username = $"channel{sourceId}",
        State = TelegramHistoryState.Start(DateTimeOffset.UnixEpoch),
        Weight = weight,
    };
}
