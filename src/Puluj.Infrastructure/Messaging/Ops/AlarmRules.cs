using Puluj.Contracts;
using Puluj.Domain.Entities.Messaging;

namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// Pure alarm rules over one ops snapshot (ADR-0012 §alarms). Stateless by design: every rule is decided from the
/// numbers in the snapshot and the SLO thresholds, so it is unit-testable and the same on every admin replica. A lane
/// an operator paused or drained is never silent (`lane_paused`, info with actor/reason) and never an error: the
/// backlog is expected there.
/// </summary>
public static class AlarmRules
{
    public const string Info = "info", Warn = "warn", Error = "error";

    public static IReadOnlyList<AlarmDto> Evaluate(MessagingOpsDto s, OpsOptions.SloOptions slo)
    {
        var alarms = new List<AlarmDto>();

        foreach (var lane in s.Subscriptions)
        {
            var scope = $"{lane.Subscription}/{lane.Lane}";
            var operated = lane.LaneState.State != SubscriptionLane.Active;
            if (operated)
            {
                alarms.Add(new AlarmDto("lane_paused", Info, scope,
                    $"{lane.Lane} lane підписки {lane.Subscription}: {lane.LaneState.State} ({lane.LaneState.Actor ?? "?"}: {lane.LaneState.Reason ?? "-"}); pending {lane.Pending}"));
            }
            if (lane.Pending > 0 && lane.Expected5m > 0 && lane.Completed5m == 0 && !operated)
            {
                alarms.Add(new AlarmDto("backlog_growing", Warn, scope, $"{lane.Pending} pending, за 5 хв очікується {lane.Expected5m}, завершено 0"));
            }
            var threshold = slo.OldestAgeSeconds.TryGetValue(lane.Lane, out var t) ? t : slo.OldestAgeSeconds.Values.DefaultIfEmpty(300).Max();
            if (lane.OldestPendingAgeSeconds is { } age && age > threshold)
            {
                alarms.Add(new AlarmDto("oldest_age_slo", operated ? Info : lane.Required ? Error : Warn, scope,
                    $"найстаріша очікувана доставка {Age(age)} (SLO {Age(threshold)}), pending {lane.Pending}{(operated ? $"; lane {lane.LaneState.State}" : "")}"));
            }
            if (lane.Required && lane.RegistryStatus == "active" && !operated && lane.Consumers == 0 && lane.Pending > 0
                && lane.OldestPendingAgeSeconds is { } waiting && waiting > slo.RequiredConsumerMissingSeconds)
            {
                alarms.Add(new AlarmDto("required_consumer_missing", Error, scope, $"обовʼязкова підписка без живого консюмера lane {lane.Lane}: {lane.Pending} pending, найстаріша {Age(waiting)}"));
            }
            if (lane.Quarantined > 0)
            {
                alarms.Add(new AlarmDto("dlq", Warn, scope, $"{lane.Quarantined} у карантині (DLQ) — потрібне рішення оператора: retry або waive"));
            }
            if (lane.InFlight > 0 && lane.OldestRunningAttemptAgeSeconds is { } running && running > slo.InflightStuckSeconds)
            {
                alarms.Add(new AlarmDto("inflight_stuck", Error, scope, $"{lane.InFlight} in-flight, найстаріший running attempt {Age(running)} (SLO {Age(slo.InflightStuckSeconds)}), завершень за 5 хв {lane.Completed5m} — черга не порожня"));
            }
        }

        foreach (var w in s.Workers)
        {
            if (w.Stuck)
            {
                alarms.Add(new AlarmDto("stale_heartbeat_with_jobs", Error, $"worker:{w.Name}", $"heartbeat застарів ({(w.HeartbeatAt is { } h ? Age((s.At - h).TotalSeconds) : "немає")}), але {w.RunningAttempts} attempts ще running"));
            }
            if (w.Llm is { PausedUntil: { } until })
            {
                alarms.Add(new AlarmDto("llm_paused", Warn, $"worker:{w.Name}", $"LLM призупинено до {until:HH:mm:ss}Z: {w.Llm.PauseReason ?? "-"}"));
            }
            if (w.Broker is { Connected: false } && !w.Stale)
            {
                alarms.Add(new AlarmDto("broker_disconnected", Error, $"worker:{w.Name}", $"процес без зʼєднання з брокером ({w.Broker.Endpoint})"));
            }
        }

        if (s.Outbox.Unconfirmed > 0 && s.Outbox.OldestAgeSeconds is { } oldest && oldest > slo.OutboxUnconfirmedSeconds)
        {
            alarms.Add(new AlarmDto("outbox_stuck", oldest > slo.OutboxCriticalSeconds ? Error : Warn, "outbox",
                $"{s.Outbox.Unconfirmed} непідтверджених рядків outbox, найстаріший {Age(oldest)} (поріг {Age(slo.OutboxUnconfirmedSeconds)}) — relay або брокер?"));
        }
        if (s.Outbox.Unroutable > 0)
        {
            alarms.Add(new AlarmDto("outbox_unroutable", Error, "outbox", $"{s.Outbox.Unroutable} unroutable публікацій (basic.return): binding відсутній — reconciliation re-declare"));
        }
        if (s.Broker.Management is { Available: true } m)
        {
            foreach (var node in m.Nodes)
            {
                if (!node.Running || node.MemAlarm || node.DiskAlarm)
                {
                    alarms.Add(new AlarmDto("broker_blocked", Error, $"broker:{node.Name}",
                        !node.Running ? "вузол брокера не працює" : $"publisher заблоковано: {(node.MemAlarm ? "memory alarm" : "")}{(node.MemAlarm && node.DiskAlarm ? ", " : "")}{(node.DiskAlarm ? "disk alarm" : "")}"));
                }
            }
        }
        if (s.Reconciliation is { } r)
        {
            if (r.OverdueCount > 0 || r.UnknownSubscriptions.Count > 0 || r.DeclareFailed.Count > 0)
            {
                alarms.Add(new AlarmDto("reconciliation_mismatch", Warn, "reconciliation",
                    $"overdue {r.OverdueCount}, невідомі підписки [{string.Join(",", r.UnknownSubscriptions)}], declare failed [{string.Join(",", r.DeclareFailed)}] ({r.At:HH:mm:ss}Z)"));
            }
        }
        if (s.Roots.NeedsAttention > 0)
        {
            alarms.Add(new AlarmDto("roots_need_attention", Warn, "roots", $"{s.Roots.NeedsAttention} повідомлень з гілкою у карантині"));
        }

        return alarms.OrderBy(a => Rank(a.Severity)).ThenBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.Scope, StringComparer.Ordinal).ToList();
    }

    private static int Rank(string severity) => severity switch { Error => 0, Warn => 1, _ => 2 };

    public static string Age(double seconds) =>
        seconds < 90 ? $"{seconds:0} с" : seconds < 5400 ? $"{seconds / 60:0} хв" : seconds < 172800 ? $"{seconds / 3600:0.#} год" : $"{seconds / 86400:0.#} д";
}
