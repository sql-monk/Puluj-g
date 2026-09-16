using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Contracts;
using Puluj.Domain.Entities.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// The message-platform operations snapshot (P13, ADR-0012, plan §9): every subscription × lane with its backlog,
/// in-flight, retries, quarantine, ages and percentiles from the receipts (`processing.deliveries`/`attempts`/
/// `quarantine`), the outbox/inbox figures, the workers with their consumer lanes from their status documents, the
/// last reconciliation report, and the alarms evaluated over all of it. Everything comes from the database — the
/// monitoring does not depend on the bus (§9.2); the broker's management API adds `ready`/`unacked` per queue when
/// configured. One computation is shared for <see cref="OpsOptions.SloOptions.SnapshotCacheSeconds"/>.
/// </summary>
public sealed class OpsSnapshotService(
    IDbContextFactory<PulujDbContext> factory,
    TopologyRegistrar registrar,
    SettingsStore settings,
    BrokerManagementClient management,
    IOptions<OpsOptions> options,
    TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cached? _cached; // a reference: readers outside the gate see either the old or the new snapshot, never a torn one

    private sealed record Cached(DateTimeOffset At, MessagingOpsDto Snapshot);

    public OpsOptions.SloOptions Slo => options.Value.Slo;

    public async Task<MessagingOpsDto> SnapshotAsync(CancellationToken ct, bool fresh = false)
    {
        var now = clock.GetUtcNow();
        if (!fresh && _cached is { } c && now - c.At < TimeSpan.FromSeconds(Slo.SnapshotCacheSeconds))
        {
            return c.Snapshot;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (!fresh && _cached is { } again && now - again.At < TimeSpan.FromSeconds(Slo.SnapshotCacheSeconds))
            {
                return again.Snapshot;
            }
            var snapshot = await ComputeAsync(ct);
            _cached = new Cached(clock.GetUtcNow(), snapshot);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class LaneAgg
    {
        public long Pending, InFlight, RetryHour, AdminRetryHour, Quarantined, ExpectedHour, CompletedHour, NoopHour, FailedHour, Expected5m, Completed5m;
        public double? OldestAge, EventLag, OldestRunning, WaitP50, WaitP95, WaitP99, ProcP50, ProcP95, ProcP99;
    }

    private sealed record WorkerAgg(long Running, long CompletedHour, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastErrorAt, string? LastError);

    private async Task<MessagingOpsDto> ComputeAsync(CancellationToken ct)
    {
        await registrar.EnsureRegisteredAsync(ct);
        var registry = registrar.Registry;
        var now = clock.GetUtcNow();
        var slo = Slo;

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);
        await Exec(conn, tx, "SET LOCAL statement_timeout = '10s'", ct);

        var statuses = await TopologyRegistrar.StatusesAsync(conn, tx, registry.TopologyVersion, ct);
        var laneStates = new Dictionary<(string, string), LaneStateDto>();
        await using (var cmd = new NpgsqlCommand("SELECT subscription_id, lane, state, reason, actor, changed_at FROM messaging.subscription_lanes", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                laneStates[(reader.GetString(0), reader.GetString(1))] = new LaneStateDto(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
            }
        }

        // Deliveries: backlog and ages (partial index on outcome IS NULL), the last hour of expectations/completions (BRIN).
        var agg = new Dictionary<(string, string), LaneAgg>();
        LaneAgg Agg(string s, string l) => agg.TryGetValue((s, l), out var a) ? a : agg[(s, l)] = new LaneAgg();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT subscription_id, coalesce(lane, '?'),
                   count(*) FILTER (WHERE outcome IS NULL),
                   extract(epoch FROM now() - min(expected_at) FILTER (WHERE outcome IS NULL))::float8,
                   extract(epoch FROM now() - min(occurred_at) FILTER (WHERE outcome IS NULL))::float8,
                   count(*) FILTER (WHERE expected_at >= now() - interval '1 hour'),
                   count(*) FILTER (WHERE completed_at >= now() - interval '1 hour' AND outcome = 'completed'),
                   count(*) FILTER (WHERE completed_at >= now() - interval '1 hour' AND outcome = 'noop'),
                   count(*) FILTER (WHERE completed_at >= now() - interval '1 hour' AND outcome = 'quarantined'),
                   count(*) FILTER (WHERE expected_at >= now() - interval '5 minutes'),
                   count(*) FILTER (WHERE completed_at >= now() - interval '5 minutes' AND outcome IN ('completed', 'noop'))
            FROM processing.deliveries
            WHERE outcome IS NULL OR completed_at >= now() - interval '1 hour' OR expected_at >= now() - interval '1 hour'
            GROUP BY 1, 2
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var a = Agg(reader.GetString(0), reader.GetString(1));
                a.Pending = reader.GetInt64(2);
                a.OldestAge = Nullable(reader, 3);
                a.EventLag = Nullable(reader, 4);
                a.ExpectedHour = reader.GetInt64(5);
                a.CompletedHour = reader.GetInt64(6);
                a.NoopHour = reader.GetInt64(7);
                a.FailedHour = reader.GetInt64(8);
                a.Expected5m = reader.GetInt64(9);
                a.Completed5m = reader.GetInt64(10);
            }
        }

        // Attempts of the last hour (BRIN on started_at) plus everything still running: in-flight, retries, processing percentiles.
        // The llm-worker's job pseudo-subscription (`llm-worker:job`) folds into its subscription.
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT split_part(a.subscription_id, ':', 1), coalesce(d.lane, '?'),
                   count(*) FILTER (WHERE a.state = 'running'),
                   extract(epoch FROM now() - min(a.started_at) FILTER (WHERE a.state = 'running'))::float8,
                   count(*) FILTER (WHERE a.state IN ('failed', 'interrupted') AND a.finished_at >= now() - interval '1 hour'),
                   count(*) FILTER (WHERE a.retry_of_attempt_id IS NOT NULL AND a.started_at >= now() - interval '1 hour'),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY (extract(epoch FROM a.finished_at - a.started_at) * 1000)::float8) FILTER (WHERE a.state = 'succeeded' AND a.finished_at >= now() - interval '1 hour'),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY (extract(epoch FROM a.finished_at - a.started_at) * 1000)::float8) FILTER (WHERE a.state = 'succeeded' AND a.finished_at >= now() - interval '1 hour'),
                   percentile_cont(0.99) WITHIN GROUP (ORDER BY (extract(epoch FROM a.finished_at - a.started_at) * 1000)::float8) FILTER (WHERE a.state = 'succeeded' AND a.finished_at >= now() - interval '1 hour')
            FROM processing.attempts a
            LEFT JOIN processing.deliveries d ON d.event_id = a.event_id AND d.subscription_id = split_part(a.subscription_id, ':', 1)
            WHERE a.started_at >= now() - interval '1 hour' OR a.state = 'running'
            GROUP BY 1, 2
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var a = Agg(reader.GetString(0), reader.GetString(1));
                a.InFlight = reader.GetInt64(2);
                a.OldestRunning = Nullable(reader, 3);
                a.RetryHour = reader.GetInt64(4);
                a.AdminRetryHour = reader.GetInt64(5);
                a.ProcP50 = Nullable(reader, 6);
                a.ProcP95 = Nullable(reader, 7);
                a.ProcP99 = Nullable(reader, 8);
            }
        }

        // Wait = first attempt of the delivery − expected_at (retries do not count twice).
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT subscription_id, lane,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY wait_ms), percentile_cont(0.95) WITHIN GROUP (ORDER BY wait_ms), percentile_cont(0.99) WITHIN GROUP (ORDER BY wait_ms)
            FROM (SELECT d.subscription_id, coalesce(d.lane, '?') AS lane, (extract(epoch FROM min(a.started_at) - d.expected_at) * 1000)::float8 AS wait_ms
                  FROM processing.attempts a
                  JOIN processing.deliveries d ON d.event_id = a.event_id AND d.subscription_id = split_part(a.subscription_id, ':', 1)
                  WHERE a.started_at >= now() - interval '1 hour'
                  GROUP BY d.subscription_id, d.lane, d.event_id, d.expected_at) w
            GROUP BY 1, 2
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var a = Agg(reader.GetString(0), reader.GetString(1));
                a.WaitP50 = Nullable(reader, 2);
                a.WaitP95 = Nullable(reader, 3);
                a.WaitP99 = Nullable(reader, 4);
            }
        }

        await using (var cmd = new NpgsqlCommand("SELECT subscription_id, lane, count(*) FROM processing.quarantine WHERE resolved_at IS NULL GROUP BY 1, 2", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                Agg(reader.GetString(0), reader.GetString(1)).Quarantined = reader.GetInt64(2);
            }
        }

        // Workers: heartbeats + status documents (consumers, broker), attempts per worker.
        var all = await settings.GetAllAsync(ct);
        var heartbeats = WorkerStatusDocuments.Heartbeats(all, now);
        var workerAgg = new Dictionary<string, WorkerAgg>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT split_part(worker, '@', 2), count(*) FILTER (WHERE state = 'running'),
                   count(*) FILTER (WHERE state = 'succeeded' AND finished_at >= now() - interval '1 hour'),
                   max(finished_at) FILTER (WHERE state = 'succeeded'),
                   max(finished_at) FILTER (WHERE state = 'failed'),
                   (array_agg(error ORDER BY finished_at DESC) FILTER (WHERE state = 'failed' AND error IS NOT NULL))[1]
            FROM processing.attempts
            WHERE started_at >= now() - interval '1 hour' OR state = 'running'
            GROUP BY 1
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                workerAgg[reader.GetString(0)] = new WorkerAgg(reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }
        var workers = new List<WorkerOpsDto>();
        foreach (var (name, heartbeat) in heartbeats)
        {
            var status = WorkerStatusDocuments.Status(all, name);
            var stale = now - heartbeat > TimeSpan.FromSeconds(slo.StaleHeartbeatSeconds);
            var host = status?.Host.ToLowerInvariant() ?? name;
            var wa = workerAgg.GetValueOrDefault(host) ?? workerAgg.GetValueOrDefault(name);
            workers.Add(new WorkerOpsDto(name, heartbeat, status?.At, stale, stale && (wa?.Running ?? 0) > 0, wa?.Running ?? 0, wa?.CompletedHour ?? 0,
                wa?.LastSuccessAt, wa?.LastErrorAt, wa?.LastError, status?.Roles ?? [], status?.Broker, status?.Llm, status?.Consumers ?? []));
        }
        // A worker with running attempts but no heartbeat at all (key removed on a clean stop, or a foreign host): still shown, as stuck.
        foreach (var (host, wa) in workerAgg)
        {
            if (wa.Running > 0 && !workers.Any(w => w.Name.Equals(host, StringComparison.OrdinalIgnoreCase) || (WorkerStatusDocuments.Status(all, w.Name)?.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                workers.Add(new WorkerOpsDto(host, null, null, true, true, wa.Running, wa.CompletedHour, wa.LastSuccessAt, wa.LastErrorAt, wa.LastError, [], null, null, []));
            }
        }
        var liveConsumers = workers.Where(w => !w.Stale).SelectMany(w => w.Consumers.Where(c => c.Consuming))
            .GroupBy(c => (c.Subscription, c.Lane)).ToDictionary(g => g.Key, g => g.Count());

        // Broker: what the workers say + the management API when configured.
        var brokerWorkers = workers.Where(w => !w.Stale && w.Broker is not null).ToList();
        var mgmt = await management.ReadAsync(ct);
        var broker = new BrokerOpsDto(
            brokerWorkers.Count == 0 ? null : brokerWorkers.Any(w => w.Broker!.Connected),
            brokerWorkers.Where(w => w.Broker!.Connected).Select(w => w.Name).ToList(),
            brokerWorkers.Where(w => !w.Broker!.Connected).Select(w => w.Name).ToList(),
            management.Configured ? new BrokerManagementDto(mgmt.Available, mgmt.Reason, mgmt.Nodes) : null);

        var subscriptions = new List<SubscriptionLaneOpsDto>();
        foreach (var subscription in registry.Subscriptions.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var lane in subscription.Lanes)
            {
                var a = agg.GetValueOrDefault((subscription.Id, lane)) ?? new LaneAgg();
                var unknownLane = agg.GetValueOrDefault((subscription.Id, "?")); // rows written before the lane column existed
                var state = laneStates.GetValueOrDefault((subscription.Id, lane)) ?? new LaneStateDto(SubscriptionLane.Active, null, null, null);
                var queue = registry.QueueName(subscription.Id, lane);
                var q = mgmt.Available && mgmt.Queues.TryGetValue(queue, out var mq) ? mq : null;
                subscriptions.Add(new SubscriptionLaneOpsDto(subscription.Id, lane, subscription.Required, statuses.GetValueOrDefault(subscription.Id, subscription.Status), state,
                    a.Pending + (lane == subscription.Lanes[0] ? unknownLane?.Pending ?? 0 : 0), a.InFlight, a.RetryHour, a.AdminRetryHour, a.Quarantined,
                    a.OldestAge, a.EventLag, a.OldestRunning, a.WaitP50, a.WaitP95, a.WaitP99, a.ProcP50, a.ProcP95, a.ProcP99,
                    a.ExpectedHour, a.CompletedHour, a.NoopHour, a.FailedHour, a.Expected5m, a.Completed5m,
                    liveConsumers.GetValueOrDefault((subscription.Id, lane)),
                    q?.Ready, q?.Unacked, q?.Consumers, q is null ? "db" : "management"));
            }
        }

        // Roots (§9.1): raw messages, not stage jobs. Pending/attention through the archived events of the raw message.
        RootsOpsDto roots;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM raw_messages WHERE received_at >= now() - interval '1 hour'),
                   (SELECT count(*) FROM (SELECT DISTINCT e.raw_message_id FROM messaging.events e WHERE e.raw_message_id IS NOT NULL AND e.published_at >= now() - interval '1 hour'
                      AND NOT EXISTS (SELECT 1 FROM messaging.events e2 JOIN processing.deliveries d ON d.event_id = e2.event_id WHERE e2.raw_message_id = e.raw_message_id AND (d.outcome IS NULL OR d.outcome = 'quarantined'))) c),
                   (SELECT count(DISTINCT e.raw_message_id) FROM processing.deliveries d JOIN messaging.events e ON e.event_id = d.event_id WHERE d.outcome IS NULL AND e.raw_message_id IS NOT NULL),
                   (SELECT count(DISTINCT e.raw_message_id) FROM processing.quarantine q JOIN messaging.events e ON e.event_id = q.event_id WHERE q.resolved_at IS NULL AND e.raw_message_id IS NOT NULL)
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            roots = new RootsOpsDto(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        OutboxOpsDto outbox;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE confirmed_at IS NULL),
                   extract(epoch FROM now() - min(created_at) FILTER (WHERE confirmed_at IS NULL))::float8,
                   count(*) FILTER (WHERE confirmed_at IS NULL AND attempts > 1),
                   count(*) FILTER (WHERE confirmed_at IS NULL AND last_error ILIKE 'basic.return%'),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY (extract(epoch FROM confirmed_at - created_at) * 1000)::float8) FILTER (WHERE confirmed_at >= now() - interval '1 hour'),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY (extract(epoch FROM confirmed_at - created_at) * 1000)::float8) FILTER (WHERE confirmed_at >= now() - interval '1 hour'),
                   count(*) FILTER (WHERE confirmed_at >= now() - interval '1 hour')
            FROM messaging.outbox
            WHERE confirmed_at IS NULL OR confirmed_at >= now() - interval '1 hour'
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            outbox = new OutboxOpsDto(reader.GetInt64(0), Nullable(reader, 1), reader.GetInt64(2), reader.GetInt64(3), Nullable(reader, 4), Nullable(reader, 5), reader.GetInt64(6));
        }

        InboxOpsDto inbox;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM messaging.inbox WHERE received_at >= now() - interval '1 hour'),
                   (SELECT count(*) FROM processing.attempts WHERE state = 'superseded' AND finished_at >= now() - interval '1 hour'),
                   (SELECT count(*) FROM messaging.inbox WHERE outcome = 'processing')
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            inbox = new InboxOpsDto(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        var backfill = new List<BackfillOpsDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT s.code, c.last_source_message_id, c.cursor::text, c.last_success_at, c.consecutive_failures, c.last_error
            FROM collector_states c JOIN sources s ON s.source_id = c.source_id
            WHERE s.enabled ORDER BY s.code
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                backfill.Add(new BackfillOpsDto(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : Truncate(reader.GetString(2), 200),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : Truncate(reader.GetString(5), 300)));
            }
        }
        await tx.RollbackAsync(ct);

        var sloDto = new OpsSloDto(slo.OldestAgeSeconds, slo.OutboxUnconfirmedSeconds, slo.OutboxCriticalSeconds, slo.StaleHeartbeatSeconds, slo.InflightStuckSeconds, slo.RequiredConsumerMissingSeconds);
        var snapshot = new MessagingOpsDto(now, registry.TopologyVersion, subscriptions, roots, broker, outbox, inbox, WorkerStatusDocuments.Reconciliation(all), workers, backfill, [], sloDto);
        return snapshot with { Alarms = AlarmRules.Evaluate(snapshot, slo) };
    }

    private static double? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
