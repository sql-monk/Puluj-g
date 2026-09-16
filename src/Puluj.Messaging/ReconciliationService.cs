using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using Puluj.Contracts;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.Messaging;

/// <summary>
/// Producer-side reconciliation (ADR-0002, plan §3.2/§15.2) on a timer: expected deliveries without a terminal receipt
/// past the SLO, outbox rows unconfirmed past the alarm threshold, receipts from subscriptions the registry does not
/// know, open quarantine; an idempotent re-declare of the topology (repairs a dropped binding, W11); and bounded
/// retention cleanup — confirmed outbox rows after the grace period, but replay-source events only once the archive
/// has completed them (§15.2), completed inbox rows after the redelivery window. Alarms are logs + gauges here; the
/// operator screen is P13.
/// </summary>
public sealed class ReconciliationService(
    TopologyDeclarer declarer,
    TopologyRegistrar registrar,
    IDbContextFactory<PulujDbContext> factory,
    IOptions<MessagingOptions> options,
    MessagingMetrics metrics,
    SettingsStore settings,
    ILogger<ReconciliationService> logger,
    string? worker = null) : BackgroundService
{
    /// <summary>`app_settings` key of the last report (P13): the admin panel reads it, the messaging process writes it after every pass.</summary>
    public const string ReportKey = "Reconciliation:Report";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _worker = worker ?? Environment.MachineName.ToLowerInvariant();

    /// <summary>Last report of this process (null before the first pass).</summary>
    public Report? LastReport { get; private set; }

    public sealed record OverdueDelivery(Guid EventId, string SubscriptionId, int TopologyVersion, DateTimeOffset ExpectedAt);

    public sealed record Report(
        long OutboxUnconfirmed,
        TimeSpan OutboxOldestAge,
        long OverdueCount,
        IReadOnlyList<OverdueDelivery> OverdueDeliveries,
        IReadOnlyList<string> UnknownSubscriptions,
        long QuarantineOpen,
        IReadOnlyList<string> DeclareFailed,
        int OutboxDeleted,
        int InboxDeleted);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = options.Value.Reconciliation.Interval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await registrar.EnsureRegisteredAsync(ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation pass failed");
            }
            await Task.Delay(interval, ct);
        }
    }

    public async Task<Report> RunOnceAsync(CancellationToken ct, bool cleanup = true, bool redeclare = true)
    {
        var o = options.Value.Reconciliation;
        var registry = registrar.Registry;
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        long unconfirmed;
        TimeSpan oldest;
        await using (var cmd = new NpgsqlCommand("SELECT count(*), coalesce(extract(epoch FROM now() - min(created_at)), 0) FROM messaging.outbox WHERE confirmed_at IS NULL", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            unconfirmed = reader.GetInt64(0);
            oldest = TimeSpan.FromSeconds(reader.GetDouble(1));
        }

        long overdueCount;
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM processing.deliveries WHERE outcome IS NULL AND expected_at < now() - @overdue", conn))
        {
            cmd.Parameters.AddWithValue("overdue", o.DeliveryOverdue);
            overdueCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var overdue = new List<OverdueDelivery>(); // sample for the log, oldest first
        await using (var cmd = new NpgsqlCommand(
            "SELECT event_id, subscription_id, topology_version, expected_at FROM processing.deliveries WHERE outcome IS NULL AND expected_at < now() - @overdue ORDER BY expected_at LIMIT 1000", conn))
        {
            cmd.Parameters.AddWithValue("overdue", o.DeliveryOverdue);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                overdue.Add(new OverdueDelivery(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        var unknown = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT DISTINCT subscription_id FROM processing.deliveries WHERE outcome IS NOT NULL AND NOT (subscription_id = ANY(@known))", conn))
        {
            cmd.Parameters.AddWithValue("known", registry.Subscriptions.Keys.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                unknown.Add(reader.GetString(0));
            }
        }

        long quarantineOpen;
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM processing.quarantine WHERE resolved_at IS NULL", conn))
        {
            quarantineOpen = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        IReadOnlyList<string> declareFailed = [];
        if (redeclare)
        {
            try
            {
                declareFailed = (await declarer.DeclareAsync(ct)).Failed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconciliation: topology re-declare failed (broker unreachable?)");
                declareFailed = ["<broker unreachable>"];
            }
        }

        var outboxDeleted = 0;
        var inboxDeleted = 0;
        if (cleanup)
        {
            await using (var cmd = new NpgsqlCommand(
                """
                DELETE FROM messaging.outbox WHERE outbox_id IN (
                    SELECT o.outbox_id FROM messaging.outbox o
                    WHERE o.confirmed_at < now() - @grace
                      AND (NOT o.replay_source OR EXISTS (
                            SELECT 1 FROM processing.deliveries d
                            WHERE d.event_id = o.event_id AND d.subscription_id = @archive AND d.outcome IN ('completed', 'noop')))
                    LIMIT @batch)
                """, conn))
            {
                cmd.Parameters.AddWithValue("grace", o.OutboxGrace);
                cmd.Parameters.AddWithValue("archive", ArchiveHandler.Subscription);
                cmd.Parameters.AddWithValue("batch", o.CleanupBatch);
                outboxDeleted = await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand(
                """
                DELETE FROM messaging.inbox WHERE (subscription_id, event_id) IN (
                    SELECT subscription_id, event_id FROM messaging.inbox
                    WHERE completed_at < now() - @retention AND outcome <> 'quarantined'
                    LIMIT @batch)
                """, conn))
            {
                cmd.Parameters.AddWithValue("retention", o.InboxRetention);
                cmd.Parameters.AddWithValue("batch", o.CleanupBatch);
                inboxDeleted = await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        metrics.Reconciled(unconfirmed, oldest.TotalSeconds, overdueCount, quarantineOpen);
        if (unconfirmed > 0 && oldest > o.OutboxOverdue)
        {
            logger.LogError("ALARM outbox: {Count} unconfirmed rows, oldest {Age} (threshold {Threshold}) — relay or broker down?", unconfirmed, oldest, o.OutboxOverdue);
        }
        if (overdue.Count > 0)
        {
            var bySubscription = overdue.GroupBy(d => d.SubscriptionId).Select(g => $"{g.Key}={g.Count()}");
            logger.LogError("ALARM deliveries: {Count} expected without receipt older than {Overdue}: {BySubscription} (oldest {Oldest:O})", overdueCount, o.DeliveryOverdue, string.Join(", ", bySubscription), overdue[0].ExpectedAt);
        }
        if (unknown.Count > 0)
        {
            logger.LogWarning("AUDIT receipts from subscriptions unknown to topology v{Version}: {Subscriptions}", registry.TopologyVersion, string.Join(",", unknown));
        }
        if (declareFailed.Count > 0)
        {
            logger.LogError("ALARM topology: declare failed for {Queues}", string.Join(",", declareFailed));
        }
        logger.LogDebug("Reconciliation: outbox unconfirmed {Unconfirmed} (oldest {Oldest}), overdue {Overdue}, quarantine {Quarantine}, cleanup outbox {OutboxDeleted}/inbox {InboxDeleted}",
            unconfirmed, oldest, overdueCount, quarantineOpen, outboxDeleted, inboxDeleted);
        var report = new Report(unconfirmed, oldest, overdueCount, overdue, unknown, quarantineOpen, declareFailed, outboxDeleted, inboxDeleted);
        LastReport = report;
        try
        {
            var dto = new ReconciliationReportDto(DateTimeOffset.UtcNow, _worker, unconfirmed, oldest.TotalSeconds, overdueCount, unknown, quarantineOpen, declareFailed, outboxDeleted, inboxDeleted);
            await settings.SetStatusAsync(ReportKey, JsonSerializer.Serialize(dto, Json), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Reconciliation report write failed");
        }
        return report;
    }
}
