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

    public static readonly string[] AllRoles = [Migrate, Telegram, Alerts, Processing, Relay, Archive, RawWriter];
    /// <summary>Roles that talk to the broker: skipped with a warning unless Messaging:Enabled (a plain local run has no RabbitMQ).</summary>
    public static readonly string[] BrokerRoles = [Relay, Archive, RawWriter];

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
