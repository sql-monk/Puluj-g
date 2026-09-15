using Npgsql;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>Archive handler that fails a configurable number of times first (transient) — W6a, W12a.</summary>
public sealed class FaultyArchiveHandler(ArchiveHandler inner, int failures, bool inApply = true) : IDeliveryHandler
{
    private int _failed;

    public string SubscriptionId => inner.SubscriptionId;
    public int Failed => Volatile.Read(ref _failed);
    /// <summary>int.MaxValue = always fail.</summary>
    public int Failures { get; set; } = failures;

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        if (!inApply)
        {
            Fail();
        }
        return await inner.PrepareAsync(envelope, ct);
    }

    public Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        if (inApply)
        {
            Fail();
        }
        return inner.ApplyAsync(conn, tx, envelope, state, ct);
    }

    private void Fail()
    {
        if (Volatile.Read(ref _failed) < Failures)
        {
            Interlocked.Increment(ref _failed);
            // The kind of failure a DB outage between work and commit produces (W12a): transient, retried.
            throw new NpgsqlException("simulated: connection reset by peer (DB outage before commit)");
        }
    }
}

/// <summary>Crash hooks that fire once per point and count.</summary>
public sealed class OnceHooks : ConsumerHooks
{
    private readonly HashSet<string> _armed = new(StringComparer.Ordinal);
    public List<string> Fired { get; } = [];

    public OnceHooks Arm(params string[] points)
    {
        foreach (var p in points)
        {
            _armed.Add(p);
        }
        return this;
    }

    private void Fire(string point)
    {
        lock (_armed)
        {
            if (_armed.Remove(point))
            {
                Fired.Add(point);
                throw new SimulatedCrashException(point);
            }
        }
    }

    public override void BeforeCommit(Envelope envelope) => Fire(nameof(BeforeCommit));
    public override void AfterCommitBeforeAck(Envelope envelope) => Fire(nameof(AfterCommitBeforeAck));
    public override void BeforeQuarantineCommit(Envelope? envelope) => Fire(nameof(BeforeQuarantineCommit));
    public override void AfterQuarantineCommitBeforeNack(Envelope? envelope) => Fire(nameof(AfterQuarantineCommitBeforeNack));
}

/// <summary>Raw-writer whose post-commit side effect throws (N8): the committed result must still be acknowledged.</summary>
public sealed class ThrowingAfterCommitHandler(RawWriterHandler inner) : IDeliveryHandler
{
    public string SubscriptionId => inner.SubscriptionId;

    public Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct) => inner.PrepareAsync(envelope, ct);

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var result = await inner.ApplyAsync(conn, tx, envelope, state, ct);
        return result with { AfterCommit = _ => throw new InvalidOperationException("simulated NOTIFY failure after commit") };
    }
}
