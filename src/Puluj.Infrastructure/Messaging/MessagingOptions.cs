namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// `Messaging` configuration section (P03). Everything is off by default: the legacy NOTIFY/claim pipeline keeps
/// working unchanged, the broker is only used by processes that enable it (compose profile `broker`).
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>Master switch for the broker roles (`relay`, `archive`): without it they are skipped with a warning.</summary>
    public bool Enabled { get; set; }

    public OutboxOptions Outbox { get; set; } = new();
    public BrokerOptions Broker { get; set; } = new();
    public RelayOptions Relay { get; set; } = new();
    public ConsumerOptions Consumer { get; set; } = new();
    public ReconciliationOptions Reconciliation { get; set; } = new();

    public sealed class OutboxOptions
    {
        /// <summary>
        /// DB-first bridge (plan §11): <c>RawMessageIngestor</c> commits `raw.stored` into `messaging.outbox` together with the
        /// raw row. Needs a `relay` role somewhere, otherwise the outbox only grows (reconciliation alarms on its age).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>`pipeline_version` written into every envelope; defaults to the informational assembly version.</summary>
        public string? PipelineVersion { get; set; }
    }

    public sealed class BrokerOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5672;
        public string VirtualHost { get; set; } = "/";
        public string User { get; set; } = "puluj";
        public string Password { get; set; } = "puluj";
        public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);
        /// <summary>Name shown in the broker's connection list; defaults to the worker instance name.</summary>
        public string? ClientName { get; set; }
    }

    public sealed class RelayOptions
    {
        /// <summary>Rows leased per pass; all are published concurrently and confirmed as one batch (P02: sequential confirms ≈138/s).</summary>
        public int BatchSize { get; set; } = 200;
        /// <summary>Wait for the broker's confirm of one batch; unconfirmed rows stay in the outbox and are published again.</summary>
        public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Must exceed <see cref="ConfirmTimeout"/> so a second relay cannot take the same rows while the first is still publishing.</summary>
        public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>Idle wait between passes when the outbox is empty.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
        /// <summary>Unroutable (basic.return) rows are retried only this often: a missing binding is fixed by declare/reconciliation, not by hammering.</summary>
        public TimeSpan UnroutableRetry { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class ConsumerOptions
    {
        public ushort Prefetch { get; set; } = 10;
        /// <summary>Lanes this process consumes for each of its subscriptions; empty = every lane of the subscription.</summary>
        public string[] Lanes { get; set; } = [];
        public TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Upper bound of the wait before a transient failure is requeued; the delivery stays unacked meanwhile (a prefetch slot), so it must stay far below the broker's consumer timeout. The wait runs on the channel's dispatch loop: other prefetched deliveries of that channel wait too (bounded head-of-line stall).</summary>
        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>A `running` attempt older than this belongs to a crashed process and is marked `interrupted` when the delivery comes back; younger ones may be a live replica.</summary>
        public TimeSpan StaleAttempt { get; set; } = TimeSpan.FromMinutes(5);
    }

    public sealed class ReconciliationOptions
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
        /// <summary>Expected delivery without a terminal receipt older than this is reported (ADR-0007 proposal).</summary>
        public TimeSpan DeliveryOverdue { get; set; } = TimeSpan.FromMinutes(10);
        /// <summary>Unconfirmed outbox row older than this is an alarm (ADR-0007: 60 s).</summary>
        public TimeSpan OutboxOverdue { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>Confirmed outbox rows are deleted after this grace; replay-source rows additionally need the archive receipt.</summary>
        public TimeSpan OutboxGrace { get; set; } = TimeSpan.FromDays(7);
        /// <summary>Completed inbox rows are deleted after this (≥ the longest possible redelivery window).</summary>
        public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(30);
        /// <summary>Max rows deleted per pass and per table, so cleanup never takes long locks.</summary>
        public int CleanupBatch { get; set; } = 5000;
    }
}
