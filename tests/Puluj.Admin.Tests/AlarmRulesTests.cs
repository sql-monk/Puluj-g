using Puluj.Contracts;
using Puluj.Infrastructure.Messaging.Ops;

namespace Puluj.Admin.Tests;

/// <summary>P13 O05: the alarm rules are pure functions over the snapshot — each rule, the paused-lane exception, per-lane SLO, and the explorer summary.</summary>
public sealed class AlarmRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpsOptions.SloOptions Slo = new();

    private static SubscriptionLaneOpsDto Lane(string subscription = "parser", string lane = "live", bool required = true, string state = "active", string? actor = null, string? reason = null,
        long pending = 0, long inFlight = 0, long quarantined = 0, double? oldestAge = null, double? oldestRunning = null, long expected5m = 0, long completed5m = 0, int consumers = 1) =>
        new(subscription, lane, required, "active", new LaneStateDto(state, reason, actor, state == "active" ? null : Now), pending, inFlight, 0, 0, quarantined, oldestAge, null, oldestRunning,
            null, null, null, null, null, null, 0, 0, 0, 0, expected5m, completed5m, consumers, null, null, null, "db");

    private static WorkerOpsDto Worker(string name = "processor-1", bool stale = false, long running = 0, LlmStatusDto? llm = null, BrokerStatusDto? broker = null) =>
        new(name, Now.AddSeconds(stale ? -600 : -10), Now, stale, stale && running > 0, running, 0, null, null, null, ["processor"], broker, llm, []);

    private static MessagingOpsDto Snapshot(IReadOnlyList<SubscriptionLaneOpsDto>? lanes = null, IReadOnlyList<WorkerOpsDto>? workers = null, OutboxOpsDto? outbox = null,
        BrokerManagementDto? management = null, ReconciliationReportDto? reconciliation = null, RootsOpsDto? roots = null) =>
        new(Now, 8, lanes ?? [], roots ?? new RootsOpsDto(0, 0, 0, 0), new BrokerOpsDto(true, ["messaging"], [], management), outbox ?? new OutboxOpsDto(0, null, 0, 0, null, null, 0),
            new InboxOpsDto(0, 0, 0), reconciliation, workers ?? [], [], [], new OpsSloDto(Slo.OldestAgeSeconds, 60, 300, 90, 300, 60));

    private static IEnumerable<string> Codes(MessagingOpsDto s) => AlarmRules.Evaluate(s, Slo).Select(a => $"{a.Severity}:{a.Code}@{a.Scope}");

    [Fact]
    public void Healthy_snapshot_has_no_alarms()
    {
        Assert.Empty(AlarmRules.Evaluate(Snapshot([Lane(pending: 2, expected5m: 5, completed5m: 5)], [Worker()]), Slo));
    }

    [Fact]
    public void Backlog_growing_needs_expectations_without_completions_and_a_backlog()
    {
        Assert.Contains("warn:backlog_growing@parser/live", Codes(Snapshot([Lane(pending: 10, expected5m: 10, completed5m: 0)])));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 0, expected5m: 10, completed5m: 0)])), c => c.Contains("backlog_growing"));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 10, expected5m: 0, completed5m: 0)])), c => c.Contains("backlog_growing"));
    }

    [Fact]
    public void Oldest_age_slo_is_per_lane_and_required_subscriptions_escalate()
    {
        Assert.Contains("error:oldest_age_slo@parser/live", Codes(Snapshot([Lane(pending: 1, oldestAge: 301)])));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 1, oldestAge: 301, lane: "history")])), c => c.Contains("oldest_age_slo")); // history SLO is an hour
        Assert.Contains("error:oldest_age_slo@parser/history", Codes(Snapshot([Lane(pending: 1, oldestAge: 3601, lane: "history")])));
        Assert.Contains("warn:oldest_age_slo@message-analytics/live", Codes(Snapshot([Lane("message-analytics", required: false, pending: 1, oldestAge: 301)])));
    }

    [Fact]
    public void Paused_or_draining_lane_is_info_with_actor_and_reason_never_error()
    {
        var s = Snapshot([Lane(state: "paused", actor: "ops", reason: "backfill window", pending: 500, oldestAge: 9999, expected5m: 5, completed5m: 0, consumers: 0)]);
        var alarms = AlarmRules.Evaluate(s, Slo);
        var paused = Assert.Single(alarms, a => a.Code == "lane_paused");
        Assert.Equal(AlarmRules.Info, paused.Severity);
        Assert.Contains("ops", paused.Message);
        Assert.Contains("backfill window", paused.Message);
        Assert.Contains("500", paused.Message);
        Assert.All(alarms, a => Assert.Equal(AlarmRules.Info, a.Severity)); // oldest_age_slo downgraded, backlog_growing/required_consumer_missing suppressed
        Assert.DoesNotContain(alarms, a => a.Code is "backlog_growing" or "required_consumer_missing");
        Assert.Contains(Codes(Snapshot([Lane(state: "draining", actor: "ops", reason: "finish then stop", pending: 3)])), c => c == "info:lane_paused@parser/live");
    }

    [Fact]
    public void Required_consumer_missing_needs_an_active_lane_with_an_aged_backlog_and_no_live_consumer()
    {
        Assert.Contains("error:required_consumer_missing@parser/live", Codes(Snapshot([Lane(pending: 1, oldestAge: 61, consumers: 0)])));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 1, oldestAge: 61, consumers: 1)])), c => c.Contains("required_consumer_missing"));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 1, oldestAge: 30, consumers: 0)])), c => c.Contains("required_consumer_missing"));
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 0, oldestAge: null, consumers: 0)])), c => c.Contains("required_consumer_missing"));
    }

    [Fact]
    public void Inflight_stuck_means_not_empty_even_when_nothing_is_ready()
    {
        // ready 0 (nothing pending beyond the in-flight one), but a running attempt is older than the SLO and nothing completes: the queue is NOT empty.
        Assert.Contains("error:inflight_stuck@parser/live", Codes(Snapshot([Lane(pending: 1, inFlight: 1, oldestRunning: 301, completed5m: 0)])));
        Assert.Contains("error:inflight_stuck@parser/live", Codes(Snapshot([Lane(pending: 1, inFlight: 1, oldestRunning: 301, completed5m: 4)]))); // a leftover running attempt on a busy lane still alarms
        Assert.DoesNotContain(Codes(Snapshot([Lane(pending: 1, inFlight: 1, oldestRunning: 30, completed5m: 0)])), c => c.Contains("inflight_stuck"));
    }

    [Fact]
    public void Dlq_stuck_worker_llm_pause_and_broker_rules()
    {
        Assert.Contains("warn:dlq@parser/live", Codes(Snapshot([Lane(quarantined: 2)])));
        Assert.Contains("error:stale_heartbeat_with_jobs@worker:ghost", Codes(Snapshot(workers: [Worker("ghost", stale: true, running: 3)])));
        Assert.DoesNotContain(Codes(Snapshot(workers: [Worker("idle", stale: true, running: 0)])), c => c.Contains("stale_heartbeat"));
        Assert.Contains("warn:llm_paused@worker:processor-1", Codes(Snapshot(workers: [Worker(llm: new LlmStatusDto(true, "m", Now.AddMinutes(5), "429", 1, 1))])));
        Assert.Contains("error:broker_disconnected@worker:messaging", Codes(Snapshot(workers: [Worker("messaging", broker: new BrokerStatusDto(false, "rabbitmq:5672/"))])));
        Assert.Contains("error:broker_blocked@broker:rabbit@node1", Codes(Snapshot(management: new BrokerManagementDto(true, null, [new BrokerNodeDto("rabbit@node1", true, true, false)]))));
        Assert.DoesNotContain(Codes(Snapshot(management: new BrokerManagementDto(false, "unreachable", []))), c => c.Contains("broker_blocked")); // no data ≠ alarm
    }

    [Fact]
    public void Outbox_reconciliation_and_roots_rules()
    {
        Assert.Contains("warn:outbox_stuck@outbox", Codes(Snapshot(outbox: new OutboxOpsDto(5, 61, 0, 0, null, null, 0))));
        Assert.Contains("error:outbox_stuck@outbox", Codes(Snapshot(outbox: new OutboxOpsDto(5, 301, 0, 0, null, null, 0))));
        Assert.DoesNotContain(Codes(Snapshot(outbox: new OutboxOpsDto(5, 30, 0, 0, null, null, 0))), c => c.Contains("outbox_stuck"));
        Assert.Contains("error:outbox_unroutable@outbox", Codes(Snapshot(outbox: new OutboxOpsDto(1, 5, 0, 1, null, null, 0))));
        Assert.Contains("warn:reconciliation_mismatch@reconciliation", Codes(Snapshot(reconciliation: new ReconciliationReportDto(Now, "messaging", 0, 0, 3, [], 0, [], 0, 0))));
        Assert.Contains("warn:reconciliation_mismatch@reconciliation", Codes(Snapshot(reconciliation: new ReconciliationReportDto(Now, "messaging", 0, 0, 0, ["ghost-sub"], 0, [], 0, 0))));
        Assert.DoesNotContain(Codes(Snapshot(reconciliation: new ReconciliationReportDto(Now, "messaging", 0, 0, 0, [], 0, [], 10, 10))), c => c.Contains("reconciliation"));
        Assert.Contains("warn:roots_need_attention@roots", Codes(Snapshot(roots: new RootsOpsDto(10, 8, 1, 1))));
    }

    [Fact]
    public void Alarms_are_ordered_errors_first()
    {
        var alarms = AlarmRules.Evaluate(Snapshot([Lane(quarantined: 1, state: "paused", actor: "a", reason: "r"), Lane("archive", pending: 1, oldestAge: 61, consumers: 0)]), Slo);
        Assert.Equal([AlarmRules.Error, AlarmRules.Warn, AlarmRules.Info], alarms.Select(a => a.Severity).Distinct().ToList());
    }

    [Fact]
    public void Every_command_requires_actor_and_reason_within_the_audit_widths()
    {
        Assert.Null(Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing("ops", "backfill window"));
        Assert.Equal("actor обовʼязковий", Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing("  ", "x"));
        Assert.Equal("reason обовʼязковий", Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing("ops", null));
        Assert.Contains("actor ≤ 128", Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing(new string('a', 129), "x"));
        Assert.Contains("reason ≤ 1000", Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing("ops", new string('r', 1001)));
    }

    [Fact]
    public void Queue_commands_use_explicit_audit_defaults_when_the_single_operator_omits_metadata()
    {
        Assert.Equal(("local-admin", "manual action from admin UI"), Puluj.Admin.Endpoints.MessagingOpsEndpoints.Audit(null, null));
        Assert.Equal(("ops", "backfill window"), Puluj.Admin.Endpoints.MessagingOpsEndpoints.Audit(" ops ", " backfill window "));
    }

    [Fact]
    public void Explorer_summary_classifies_branches_and_completion()
    {
        static LifecycleDeliveryDto D(string s, string? outcome) => new(s, "live", Now, outcome, null, null, null, [], false);
        var events = new List<LifecycleEventDto>
        {
            new(Guid.NewGuid(), "raw.stored", "live", Now, Now, Now, null, "raw-writer", [D("archive", "completed"), D("normalizer", null)]),
            new(Guid.NewGuid(), "message.normalized", "live", Now, Now, Now, null, "normalizer", [D("archive", "noop"), D("parser", "quarantined")]),
        };
        var summary = MessageExplorer.Summarize(events);
        Assert.Equal("needs_attention", summary.Completion);
        Assert.Equal(["normalizer/live"], summary.Waiting);
        Assert.Equal(["archive/live"], summary.Completed);
        Assert.Equal(["parser/live"], summary.Failed);
        Assert.Equal("pending", MessageExplorer.Summarize([]).Completion);
        Assert.Equal("completed", MessageExplorer.Summarize([events[0] with { Deliveries = [D("archive", "completed"), D("normalizer", "waived")] }]).Completion);
        Assert.Equal("in_progress", MessageExplorer.Summarize([events[0]]).Completion);
    }
}
