namespace Puluj.Worker.Hosting;

/// <summary>
/// Which parts of the Worker this process runs. One image, several containers: each takes a subset of the roles
/// (see deploy/docker-compose.yml). No roles configured = everything in one process (local development).
/// </summary>
public sealed class WorkerOptions
{
    public const string Section = "Worker";

    public const string Migrate = "migrate";
    public const string Telegram = "telegram";
    public const string Alerts = "alerts";
    public const string Processing = "processing";
    /// <summary>Outbox relay + topology declare + reconciliation/cleanup (P03); needs Messaging:Enabled and a broker.</summary>
    public const string Relay = "relay";
    /// <summary>Archive subscription consumer + DLQ consumer (P03); needs Messaging:Enabled and a broker.</summary>
    public const string Archive = "archive";
    /// <summary>Raw-writer subscription (P04): stores `ingress.received` as raw_messages; needs Messaging:Enabled and a broker.</summary>
    public const string RawWriter = "raw-writer";
    /// <summary>Normalizer stage subscription (P05): `raw.stored` → `message.normalized`.</summary>
    public const string Normalizer = "normalizer";
    /// <summary>Rules/structured parser stage subscription (P05): `message.normalized` → `parse.completed` / `llm.requested`.</summary>
    public const string Parser = "parser";
    /// <summary>LLM worker subscription (P06): `llm.requested` → `llm.completed`/`llm.failed` with lease/fencing and request audit.</summary>
    public const string LlmWorker = "llm-worker";
    /// <summary>Extraction finalizer subscription (P06): one canonical extraction per raw/run, `observations.recorded`, `message.analysis.completed`.</summary>
    public const string Finalizer = "finalizer";
    /// <summary>P09 domain writers (platform path; never together with `processing` on the same database — ADR-0009 cutover).</summary>
    public const string TrackWorker = "track-worker";
    public const string AlertWorker = "alert-worker";
    public const string Watchdog = "watchdog";
    public const string IncidentWorker = "incident-worker";

    public static readonly string[] AllRoles = [Migrate, Telegram, Alerts, Processing, Relay, Archive, RawWriter, Normalizer, Parser, LlmWorker, Finalizer, TrackWorker, AlertWorker, Watchdog, IncidentWorker];
    /// <summary>Roles that talk to the broker: skipped with a warning unless Messaging:Enabled (a plain local run has no RabbitMQ).</summary>
    public static readonly string[] BrokerRoles = [Relay, Archive, RawWriter, Normalizer, Parser, LlmWorker, Finalizer, TrackWorker, AlertWorker, Watchdog, IncidentWorker];

    /// <summary>Instance name for the heartbeat, logs, telemetry and claims (`worker`, `processor`, `collector-telegram`…); see <see cref="InstanceName"/>.</summary>
    public string Name { get; set; } = "worker";

    /// <summary>
    /// Appends the host name to <see cref="Name"/> for replicas started from one service definition (compose
    /// `deploy.replicas`): each container then has its own heartbeat and its own name in raw_messages.claimed_by.
    /// </summary>
    public bool AppendHostName { get; set; }

    /// <summary>Name of this running instance: <see cref="Name"/>, plus the host name when <see cref="AppendHostName"/>.</summary>
    public string InstanceName => AppendHostName ? $"{Name}-{Environment.MachineName.ToLowerInvariant()}" : Name;

    /// <summary>Comma-separated roles; empty means all of them.</summary>
    public string? Roles { get; set; }

    public IReadOnlySet<string> RoleSet
    {
        get
        {
            var roles = (Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r.ToLowerInvariant()).ToHashSet();
            var unknown = roles.Except(AllRoles).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"Unknown Worker:Roles value(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", AllRoles)}.");
            }
            return roles.Count == 0 ? AllRoles.ToHashSet() : roles;
        }
    }

    /// <summary>Only migrations and seeding: the process exits once they are applied (compose `migrate` service).</summary>
    public bool MigrateOnly => RoleSet.SetEquals([Migrate]);
}
