using Npgsql;
using Puluj.Infrastructure.Messaging;

namespace Puluj.Messaging;

/// <summary>
/// Business part of a subscription (ADR-0004 §6.2 steps 2–3). <see cref="PrepareAsync"/> runs outside any database
/// transaction (CPU, remote calls); <see cref="ApplyAsync"/> runs inside the short transaction that also writes the
/// inbox row, the delivery receipt and the outgoing events, and must only touch the database through the given
/// connection/transaction. Throw <see cref="PermanentDeliveryException"/> for input that can never succeed
/// (quarantine without retries); any other exception is transient and retried with backoff up to the policy limit.
/// </summary>
public interface IDeliveryHandler
{
    string SubscriptionId { get; }

    Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct);

    Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct);
}

/// <summary>Terminal outcome of one delivery: `completed` (effect applied) or `noop` (checked, deliberately nothing to change — still a receipt, ADR-0005).</summary>
public sealed record DeliveryResult(string Outcome, string? Reason = null, IReadOnlyList<Envelope>? OutEvents = null)
{
    public static readonly DeliveryResult Completed = new("completed");
    public static DeliveryResult Noop(string reason) => new("noop", reason);

    /// <summary>Best-effort side effect after the commit and before the ACK (a NOTIFY, a metric); a failure is logged, never retried — the effect is already durable.</summary>
    public Func<CancellationToken, Task>? AfterCommit { get; init; }

    /// <summary>`processing.stage_results.stage_result_id` written by this delivery, linked from the attempt row (ADR-0006).</summary>
    public long? StageResultId { get; init; }
}

/// <summary>The delivery cannot succeed however often it is retried: invalid payload, unknown reference — straight to quarantine.</summary>
public sealed class PermanentDeliveryException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

/// <summary>Raised by a test hook to simulate a process crash at a precise point: the channel closes without ACK, nothing else runs.</summary>
public sealed class SimulatedCrashException(string point) : Exception($"simulated crash at {point}")
{
    public string Point { get; } = point;
}

/// <summary>
/// Crash points of ADR-0004 (W4, W5, W6a-1, W6a-2) as overridable hooks; the default does nothing. Tests subclass it
/// and throw <see cref="SimulatedCrashException"/> once.
/// </summary>
public class ConsumerHooks
{
    public static readonly ConsumerHooks None = new();

    public virtual void BeforeCommit(Envelope envelope) { }
    public virtual void AfterCommitBeforeAck(Envelope envelope) { }
    public virtual void BeforeQuarantineCommit(Envelope? envelope) { }
    public virtual void AfterQuarantineCommitBeforeNack(Envelope? envelope) { }
}
