using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Puluj.Messaging;

/// <summary>
/// Publishes `messaging.outbox` rows to the broker (ADR-0004 §2, W2/W3/W7/W11/W13). One pass: lease a batch
/// (`FOR UPDATE SKIP LOCKED`, several relays may run), publish every row persistent + mandatory with publisher
/// confirms awaited concurrently, mark the confirmed ones, schedule the rest. A row is only gone from the scan once
/// `confirmed_at` is set, so a crash after confirm and before the mark publishes the same event id again and the
/// consumers' inbox absorbs it. Unroutable returns (missing binding) are not retried in a loop: the row waits for
/// declare/reconciliation to restore the binding. Business `published_at` inside the envelope never changes here.
/// </summary>
public sealed class OutboxRelay(
    BrokerConnection broker,
    TopologyDeclarer declarer,
    TopologyRegistrar registrar,
    IDbContextFactory<PulujDbContext> factory,
    IOptions<MessagingOptions> options,
    MessagingMetrics metrics,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    public string Owner { get; } = $"{Environment.MachineName.ToLowerInvariant()}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    public sealed record RelayPass(int Leased, int Confirmed, int Unroutable, int Failed, int TimedOut);

    private sealed record Row(long OutboxId, Guid EventId, string EventType, string Lane, string RoutingKey, string? TargetQueue, byte[] Body, int Attempts);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var relay = options.Value.Relay;
        ValidateOptions(relay);
        await DeclareWithRetryAsync(ct);
        logger.LogInformation("Outbox relay started: owner {Owner}, batch {Batch}, confirm timeout {Timeout}", Owner, relay.BatchSize, relay.ConfirmTimeout);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pass = await RelayOnceAsync(ct);
                if (pass.Leased == 0)
                {
                    await Task.Delay(relay.PollInterval, ct);
                }
                else if (pass.Confirmed == 0)
                {
                    await Task.Delay(relay.MinBackoff, ct); // broker or database trouble: do not spin
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay pass failed; retrying in {Delay}", relay.MinBackoff);
                await Task.Delay(relay.MinBackoff, ct);
            }
        }
    }

    private async Task DeclareWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await registrar.EnsureRegisteredAsync(ct);
                await declarer.DeclareAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Topology declare failed; broker not reachable yet? retrying in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// One relay pass. <paramref name="markConfirmed"/> = false simulates a crash between the broker's confirm and the
    /// mark (ADR-0004 W3/W12b; tests only): the rows stay leased and unconfirmed, the next pass after the lease
    /// republishes them with the same event ids.
    /// </summary>
    public async Task<RelayPass> RelayOnceAsync(CancellationToken ct, bool markConfirmed = true)
    {
        var relay = options.Value.Relay;
        ValidateOptions(relay);
        var rows = await LeaseAsync(relay, ct);
        if (rows.Count == 0)
        {
            return new RelayPass(0, 0, 0, 0, 0);
        }

        var connection = await broker.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(relay.ConfirmTimeout);
        var started = Stopwatch.GetTimestamp();
        var results = await Task.WhenAll(rows.Select(row => PublishAsync(channel, row, timeout.Token, ct)));
        var latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var confirmed = new List<long>();
        var unroutable = 0;
        var failed = 0;
        var timedOut = 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        foreach (var (row, outcome, error) in results)
        {
            switch (outcome)
            {
                case Outcome.Confirmed:
                    confirmed.Add(row.OutboxId);
                    break;
                case Outcome.Unroutable:
                    unroutable++;
                    metrics.PublishFailed("unroutable");
                    logger.LogError("Outbox {EventId} {RoutingKey} unroutable: no bound queue (missing binding, ADR-0004 W11); waiting for declare/reconciliation", row.EventId, row.TargetQueue ?? row.RoutingKey);
                    await ScheduleAsync(conn, row.OutboxId, error, relay.UnroutableRetry, ct);
                    break;
                case Outcome.TimedOut:
                    // W7b: the confirm may still arrive; the row keeps its lease and is republished after it expires.
                    timedOut++;
                    metrics.PublishFailed("timeout");
                    await ScheduleAsync(conn, row.OutboxId, error, null, ct);
                    break;
                default:
                    failed++;
                    metrics.PublishFailed(outcome == Outcome.Nacked ? "nack" : "error");
                    await ScheduleAsync(conn, row.OutboxId, error, Backoff(relay, row.Attempts), ct);
                    break;
            }
        }
        if (confirmed.Count > 0 && markConfirmed)
        {
            await using var mark = new NpgsqlCommand(
                "UPDATE messaging.outbox SET confirmed_at = now(), lease_owner = NULL, lease_until = NULL, last_error = NULL WHERE outbox_id = ANY(@ids) AND confirmed_at IS NULL", conn);
            mark.Parameters.AddWithValue("ids", confirmed.ToArray());
            await mark.ExecuteNonQueryAsync(ct);
        }
        if (confirmed.Count > 0)
        {
            metrics.Published(confirmed.Count, latencyMs);
        }
        logger.LogDebug("Relay pass: leased {Leased}, confirmed {Confirmed}, unroutable {Unroutable}, failed {Failed}, timed out {TimedOut} in {Ms:F0} ms", rows.Count, confirmed.Count, unroutable, failed, timedOut, latencyMs);
        return new RelayPass(rows.Count, confirmed.Count, unroutable, failed, timedOut);
    }

    private enum Outcome { Confirmed, Unroutable, Nacked, TimedOut, Error }

    private async Task<(Row Row, Outcome Outcome, string? Error)> PublishAsync(IChannel channel, Row row, CancellationToken timeout, CancellationToken ct)
    {
        var props = new BasicProperties
        {
            Persistent = true,
            MessageId = row.EventId.ToString(),
            ContentType = "application/json",
            Type = row.EventType,
            Headers = new Dictionary<string, object?> { ["lane"] = row.Lane },
        };
        try
        {
            if (row.TargetQueue is null)
            {
                await channel.BasicPublishAsync(registrar.Registry.ExchangeName, row.RoutingKey, mandatory: true, props, row.Body, timeout);
            }
            else
            {
                await channel.BasicPublishAsync(string.Empty, row.TargetQueue, mandatory: true, props, row.Body, timeout);
            }
            return (row, Outcome.Confirmed, null);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            var text = ex is PublishReturnException r ? $"basic.return {r.ReplyCode} {r.ReplyText} ({r.Exchange}/{r.RoutingKey})" : "basic.return";
            return (row, Outcome.Unroutable, text);
        }
        catch (PublishException ex)
        {
            return (row, Outcome.Nacked, $"broker nack: {ex.Message}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (row, Outcome.TimedOut, "confirm timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (row, Outcome.Error, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task<List<Row>> LeaseAsync(MessagingOptions.RelayOptions relay, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var lease = new NpgsqlCommand(
            """
            UPDATE messaging.outbox SET lease_owner = @owner, lease_until = now() + @lease, attempts = attempts + 1, last_attempt_at = now()
            WHERE outbox_id IN (
                SELECT outbox_id FROM messaging.outbox
                WHERE confirmed_at IS NULL AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                ORDER BY outbox_id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            RETURNING outbox_id, event_id, event_type, lane, routing_key, target_queue, envelope::text, attempts
            """, conn);
        lease.Parameters.AddWithValue("owner", Owner);
        lease.Parameters.AddWithValue("lease", relay.Lease);
        lease.Parameters.AddWithValue("limit", relay.BatchSize);
        var rows = new List<Row>();
        await using var reader = await lease.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Row(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), System.Text.Encoding.UTF8.GetBytes(reader.GetString(6)), reader.GetInt32(7)));
        }
        return rows;
    }

    /// <summary><paramref name="retryIn"/> null keeps the lease (delayed confirm may still land); otherwise the lease is released and the row waits.</summary>
    private static async Task ScheduleAsync(NpgsqlConnection conn, long outboxId, string? error, TimeSpan? retryIn, CancellationToken ct)
    {
        await using var update = retryIn is null
            ? new NpgsqlCommand("UPDATE messaging.outbox SET last_error = @error WHERE outbox_id = @id AND confirmed_at IS NULL", conn)
            : new NpgsqlCommand("UPDATE messaging.outbox SET last_error = @error, next_attempt_at = now() + @retry, lease_owner = NULL, lease_until = NULL WHERE outbox_id = @id AND confirmed_at IS NULL", conn);
        update.Parameters.AddWithValue("error", (object?)Truncate(error) ?? DBNull.Value);
        update.Parameters.AddWithValue("id", outboxId);
        if (retryIn is not null)
        {
            update.Parameters.AddWithValue("retry", retryIn.Value);
        }
        await update.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Two relays must never publish the same batch: a lease shorter than the confirm wait would let the second one lease rows the first is still confirming.</summary>
    public static void ValidateOptions(MessagingOptions.RelayOptions relay)
    {
        if (relay.Lease <= relay.ConfirmTimeout)
        {
            throw new InvalidOperationException($"Messaging:Relay:Lease ({relay.Lease}) must exceed Messaging:Relay:ConfirmTimeout ({relay.ConfirmTimeout})");
        }
    }

    /// <summary>Bounded exponential backoff with jitter (ADR-0004 §6.3); attempts already counts the failed one.</summary>
    public static TimeSpan Backoff(MessagingOptions.RelayOptions relay, int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var delay = relay.MinBackoff.TotalMilliseconds * Math.Pow(2, exponent);
        delay = Math.Min(delay, relay.MaxBackoff.TotalMilliseconds);
        delay += delay * Random.Shared.NextDouble() * 0.25;
        return TimeSpan.FromMilliseconds(delay);
    }

    private static string? Truncate(string? text) => text is { Length: > 2000 } ? text[..2000] : text;
}
