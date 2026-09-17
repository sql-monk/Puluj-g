using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Analytics;
using Puluj.Collectors;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing;
using Puluj.Processing.Indexes;
using Puluj.Processing.Llm;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Stages;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// One PostGIS and one RabbitMQ (single node, `rabbitmq:4.3-management`) for the whole collection; tests run one after
/// another and call <see cref="ResetAsync"/> first. The production DI graph (Infrastructure + Messaging with roles
/// relay+archive) is built without a host: the tests drive the relay, consumers and reconciliation themselves.
/// Infrastructure failures fail the suite — no database/broker test ever appears passed without running.
/// </summary>
public sealed class MessagingFixture : IAsyncLifetime
{
    public const string BrokerImage = "rabbitmq:4.3-management";
    private const string BrokerUser = "puluj";
    private const string BrokerPassword = "puluj-p03";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.5").WithDatabase("puluj_messaging_test").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(BrokerImage)
        .WithUsername(BrokerUser).WithPassword(BrokerPassword)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    public ServiceProvider Services { get; private set; } = null!;
    public IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    public RawMessageIngestor Ingestor => Services.GetRequiredService<RawMessageIngestor>();
    public OutboxWriter Outbox => Services.GetRequiredService<OutboxWriter>();
    public TopologyRegistrar Registrar => Services.GetRequiredService<TopologyRegistrar>();
    public TopologyRegistry Registry => Registrar.Registry;
    public TopologyDeclarer Declarer => Services.GetRequiredService<TopologyDeclarer>();
    public OutboxRelay Relay => Services.GetRequiredService<OutboxRelay>();
    public SubscriptionConsumer Archive => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == ArchiveHandler.Subscription);
    public SubscriptionConsumer RawWriter => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == RawWriterHandler.Subscription);
    public SubscriptionConsumer Normalizer => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == NormalizerHandler.Subscription);
    public SubscriptionConsumer Parser => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == ParserHandler.Subscription);
    public SubscriptionConsumer LlmWorker => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == LlmWorkerHandler.Subscription);
    public SubscriptionConsumer Finalizer => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == FinalizerHandler.Subscription);
    public SubscriptionConsumer TrackWorker => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == Puluj.Processing.Writers.TrackWriterHandler.Subscription);
    public SubscriptionConsumer AlertWorker => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == Puluj.Processing.Writers.AlertWriterHandler.Subscription);
    public Puluj.Processing.Writers.DomainWatchdog Watchdog => Services.GetRequiredService<Puluj.Processing.Writers.DomainWatchdog>();
    public SubscriptionConsumer IncidentWorker => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == Puluj.Processing.Incidents.IncidentWriterHandler.Subscription);
    /// <summary>P11: the map push adapter's durable half (incident.changed → NOTIFY IncidentChanged).</summary>
    public SubscriptionConsumer Projection => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == Puluj.Processing.Projection.ProjectionHandler.Subscription);
    /// <summary>P15: the lifecycle projection consumer — started for the whole collection (a projection without side effects; every expected `message-analytics` delivery completes).</summary>
    public SubscriptionConsumer MessageAnalytics => Services.GetServices<SubscriptionConsumer>().Single(c => c.SubscriptionId == Puluj.Processing.Analytics.MessageAnalyticsHandler.Subscription);
    public Puluj.Processing.Incidents.IncidentStateWriter Incidents => Services.GetRequiredService<Puluj.Processing.Incidents.IncidentStateWriter>();
    public FakeLlmCompletion Llm { get; } = new();
    public RawMessageProcessor LegacyProcessor => Services.GetRequiredService<RawMessageProcessor>();
    public IndexProvider Indexes => Services.GetRequiredService<IndexProvider>();
    public IngressWriter IngressWriter => Services.GetRequiredService<IngressWriter>();
    public CollectorIngress Ingress => Services.GetRequiredService<CollectorIngress>();
    public CollectorStateStore States => Services.GetRequiredService<CollectorStateStore>();
    public DlqConsumer Dlq => Services.GetRequiredService<DlqConsumer>();
    public ReconciliationService Reconciliation => Services.GetRequiredService<ReconciliationService>();
    public SubscriptionAdmin Admin => Services.GetRequiredService<SubscriptionAdmin>();
    /// <summary>P14: run/generation orchestration and the replay job runner (driven by the tests: `PublishOnceAsync`, or started as a hosted service).</summary>
    public Puluj.Infrastructure.Processing.RunService Runs => Services.GetRequiredService<Puluj.Infrastructure.Processing.RunService>();
    public ReplayPublisher Replay => Services.GetRequiredService<ReplayPublisher>();
    public BrokerConnection Broker => Services.GetRequiredService<BrokerConnection>();
    public int SourceId { get; private set; }
    public string SourceCode { get; } = "tg_kpszsu";
    public string BrokerVersion { get; private set; } = "";
    public Evidence Evidence { get; } = new();

    public MessagingOptions Options { get; } = new()
    {
        Enabled = true,
        Outbox = { Enabled = true, PipelineVersion = "p03-test" },
        Ingress = { Enabled = true, DrainTimeout = TimeSpan.FromSeconds(3) },
        RawWriter = { InsertTimeout = TimeSpan.FromSeconds(20) },
        Relay = { BatchSize = 200, ConfirmTimeout = TimeSpan.FromSeconds(2), Lease = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(200), MinBackoff = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromSeconds(2), UnroutableRetry = TimeSpan.FromMilliseconds(500) },
        Consumer = { Prefetch = 10, MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500), ControlPoll = TimeSpan.FromMilliseconds(200) },
        Reconciliation = { DeliveryOverdue = TimeSpan.Zero, OutboxOverdue = TimeSpan.FromSeconds(1), OutboxGrace = TimeSpan.FromSeconds(1), InboxRetention = TimeSpan.FromSeconds(1), CleanupBatch = 1000 },
    };

    public async Task InitializeAsync()
    {
        try
        {
            await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Real PostGIS and RabbitMQ are required (Docker). No tests were executed.", ex);
        }
        var repoRoot = FindRepoRoot();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Puluj"] = _postgres.GetConnectionString(),
            ["Seed:DataDirectory"] = Path.Combine(repoRoot, "data"),
            ["Seed:SeedGazetteer"] = "true", // the parser stage needs the gazetteer (P05)
            ["Llm:Enabled"] = "true", // the fallback decision (no API key): parse.completed{needs_llm} + llm.requested; the llm-worker talks to FakeLlmCompletion
            ["Llm:MaxMessageAgeHours"] = "72",
            ["Llm:MaxAttempts"] = "2",
            ["Llm:LeaseSeconds"] = "3",
            ["Llm:TimeoutSeconds"] = "2",
            ["Llm:FailurePause"] = "00:00:02",
            ["Messaging:Consumer:PrefetchBySubscription:llm-worker"] = "1", // a blocked replica has no credit → a duplicate command lands on the other one (F04b)
            ["Messaging:Consumer:PrefetchBySubscription:track-worker"] = "1", // W02: two replicas must each take one delivery
            ["Correlation:WatchdogInterval"] = "00:10:00", // sweeps are driven by the tests (SweepAsync), never by the timer
            ["Messaging:Enabled"] = "true",
            ["Messaging:Outbox:Enabled"] = "true",
            ["Messaging:Outbox:PipelineVersion"] = Options.Outbox.PipelineVersion,
            ["Messaging:Ingress:Enabled"] = "true",
            ["Messaging:Ingress:DrainTimeout"] = Options.Ingress.DrainTimeout.ToString(),
            ["Messaging:RawWriter:InsertTimeout"] = Options.RawWriter.InsertTimeout.ToString(),
            ["Messaging:Broker:Host"] = _rabbit.Hostname,
            ["Messaging:Broker:Port"] = _rabbit.GetMappedPublicPort(5672).ToString(),
            ["Messaging:Broker:User"] = BrokerUser,
            ["Messaging:Broker:Password"] = BrokerPassword,
            ["Messaging:Relay:BatchSize"] = Options.Relay.BatchSize.ToString(),
            ["Messaging:Relay:ConfirmTimeout"] = Options.Relay.ConfirmTimeout.ToString(),
            ["Messaging:Relay:Lease"] = Options.Relay.Lease.ToString(),
            ["Messaging:Relay:PollInterval"] = Options.Relay.PollInterval.ToString(),
            ["Messaging:Relay:MinBackoff"] = Options.Relay.MinBackoff.ToString(),
            ["Messaging:Relay:MaxBackoff"] = Options.Relay.MaxBackoff.ToString(),
            ["Messaging:Relay:UnroutableRetry"] = Options.Relay.UnroutableRetry.ToString(),
            ["Messaging:Consumer:Prefetch"] = Options.Consumer.Prefetch.ToString(),
            ["Messaging:Consumer:MinBackoff"] = Options.Consumer.MinBackoff.ToString(),
            ["Messaging:Consumer:MaxBackoff"] = Options.Consumer.MaxBackoff.ToString(),
            ["Messaging:Consumer:ControlPoll"] = Options.Consumer.ControlPoll.ToString(), // P13: pause/resume/drain react within a tick
            ["Ops:Slo:RequiredConsumerMissingSeconds"] = "0", // P13 O03: alarms fire on ages of seconds, not minutes
            ["Ops:Slo:InflightStuckSeconds"] = "0",
            ["Ops:Slo:SnapshotCacheSeconds"] = "0",
            ["Replay:BatchSize"] = "2", // P14 R02: several batches out of a handful of raw messages
            ["Replay:PollInterval"] = "00:00:00.200",
            ["Replay:WatermarkLag"] = "00:00:00",
            ["Messaging:Reconciliation:DeliveryOverdue"] = Options.Reconciliation.DeliveryOverdue.ToString(),
            ["Messaging:Reconciliation:OutboxOverdue"] = Options.Reconciliation.OutboxOverdue.ToString(),
            ["Messaging:Reconciliation:OutboxGrace"] = Options.Reconciliation.OutboxGrace.ToString(),
            ["Messaging:Reconciliation:InboxRetention"] = Options.Reconciliation.InboxRetention.ToString(),
            ["Messaging:Reconciliation:CleanupBatch"] = Options.Reconciliation.CleanupBatch.ToString(),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(repoRoot));
        services.AddPulujInfrastructure(config, "p03-test");
        services.AddPulujMessaging(new HashSet<string> { DependencyInjection.RelayRole, DependencyInjection.ArchiveRole, DependencyInjection.RawWriterRole, DependencyInjection.ReplayRole }, "p03-test");
        services.AddSingleton<ILlmCompletion>(Llm); // before AddPulujParsing: the real Anthropic completion is TryAdd'ed
        services.AddPulujProcessing(config, "p03-test"); // legacy processor for the parity test (hosted loop is never started here)
        services.AddPulujStages(config, new HashSet<string> { StageRoles.Normalizer, StageRoles.Parser, StageRoles.LlmWorker, StageRoles.Finalizer }, "p03-test");
        // P09 domain writers next to the legacy processor: never started together on the same messages (W06/W07 drive each path explicitly).
        services.AddPulujDomainWriters(config, new HashSet<string> { StageRoles.TrackWorker, StageRoles.AlertWorker, StageRoles.Watchdog, StageRoles.IncidentWorker }, "p03-test");
        services.AddPulujProjection(new HashSet<string> { StageRoles.Projection }, "p03-test"); // P11: registered the way the Worker does it (review B1)
        services.AddPulujMessageAnalytics(new HashSet<string> { StageRoles.MessageAnalytics }, "p03-test"); // P15: lifecycle projection
        services.AddPulujAnalyticsReporting(config); // P15: the analytics schema (its own migrations) and the report service
        // The collectors' entry point without the collectors themselves (Telegram needs MTProto, alerts.in.ua a token).
        services.AddSingleton<CollectorStateStore>();
        services.AddSingleton<CollectorIngress>();
        Services = services.BuildServiceProvider();

        await using (var db = await Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis");
            await db.Database.MigrateAsync(); // includes analytics.message_lifecycle (P15: DDL owned by the pipeline migrations)
            await using (var analytics = await Services.GetRequiredService<IDbContextFactory<Puluj.Analytics.Persistence.AnalyticsDbContext>>().CreateDbContextAsync())
            {
                await analytics.Database.MigrateAsync(); // the copy-analytics tables (state, runs, messages…) the report service and backfill state use
            }
            foreach (var seeder in Services.GetServices<ISeeder>().OrderBy(s => s.Order))
            {
                await seeder.SeedAsync(db, CancellationToken.None);
            }
            await EnsureDeterministicGazetteerAsync(db);
            SourceId = await db.Sources.Where(s => s.Code == SourceCode).Select(s => s.SourceId).SingleAsync();
        }
        await Indexes.RefreshAsync(CancellationToken.None);
        var connection = await Broker.GetAsync(CancellationToken.None);
        BrokerVersion = connection.ServerProperties is { } props && props.TryGetValue("version", out var v) && v is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "?";
        await ResetAsync(); // P15: ResetAsync also (re)starts the lifecycle projection consumer for the whole collection
    }

    /// <summary>
    /// Geo-boundary downloads are optional in a clean clone, whereas the message-stage suite must always exercise
    /// real PostGIS locations.  Supply a compact test-only hierarchy only when the optional import produced no
    /// regions; production datasets are never changed by this fixture.
    /// </summary>
    private static async Task EnsureDeterministicGazetteerAsync(PulujDbContext db)
    {
        if (await db.Places.AnyAsync(p => p.Level == PlaceLevel.Region))
        {
            return;
        }

        Place Region(string key, string name, string[] variants, double lon, double lat, double halfWidth = 0.45, double halfHeight = 0.35) => new()
        {
            Name = name,
            NameVariants = variants,
            Level = PlaceLevel.Region,
            CountryCode = "UA",
            ExternalKey = $"test:{key}",
            Geometry = Geo.Factory.CreatePolygon([
                new(lon - halfWidth, lat - halfHeight), new(lon + halfWidth, lat - halfHeight),
                new(lon + halfWidth, lat + halfHeight), new(lon - halfWidth, lat + halfHeight), new(lon - halfWidth, lat - halfHeight),
            ]),
            Centroid = Geo.Point(lon, lat),
            RadiusKm = 180,
        };

        var kyiv = Region("UA-32", "Київська область", ["київськ обл", "київщин", "київськ"], 30.5, 50.45);
        var poltava = Region("UA-53", "Полтавська область", ["полтавськ обл", "полтавщин", "полтавськ"], 34.55, 49.6);
        var sumy = Region("UA-59", "Сумська область", ["сумськ обл", "сумщин", "сумськ"], 34.8, 51.0, 1.0, 1.3);
        var kirovohrad = Region("UA-35", "Кіровоградська область", ["кіровоградськ обл", "кіровоградщин", "кіровоградськ"], 32.25, 48.5);
        var kharkiv = Region("UA-63", "Харківська область", ["харківськ обл", "харківщин", "харківськ"], 36.45, 49.95);
        db.Places.AddRange(kyiv, poltava, sumy, kirovohrad, kharkiv);
        await db.SaveChangesAsync();

        var brovary = new Place
        {
            Name = "Броварський район",
            NameVariants = ["броварськ район", "броварськ"],
            Level = PlaceLevel.District,
            ParentId = kyiv.PlaceId,
            CountryCode = "UA",
            ExternalKey = "test:UA-32-brovary",
            Geometry = Geo.Factory.CreatePolygon([
                new(30.5, 50.35), new(30.9, 50.35), new(30.9, 50.65), new(30.5, 50.65), new(30.5, 50.35),
            ]),
            Centroid = Geo.Point(30.7, 50.5),
            RadiusKm = 25,
        };
        db.Places.Add(brovary);
        await db.SaveChangesAsync();
        db.Places.Add(new Place
        {
            Name = "Тестова громада",
            NameVariants = ["тестов гром"],
            Level = PlaceLevel.Hromada,
            ParentId = brovary.PlaceId,
            CountryCode = "UA",
            ExternalKey = "test:UA-32-brovary-hromada",
            Geometry = Geo.Factory.CreatePolygon([
                new(30.6, 50.4), new(30.8, 50.4), new(30.8, 50.55), new(30.6, 50.55), new(30.6, 50.4),
            ]),
            Centroid = Geo.Point(30.7, 50.475),
            RadiusKm = 15,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await MessageAnalytics.StopAsync(CancellationToken.None);
        Evidence.Flush(EvidencePath("messaging-crash-evidence.json"), BrokerVersion);
        await Services.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>Empty tables of both schemas and raw_messages, re-register the topology (v2, archive active), declare it, purge the queues.</summary>
    public async Task ResetAsync()
    {
        await MessageAnalytics.StopAsync(CancellationToken.None); // P15 (review B4): drained before the TRUNCATE, restarted after the purge — no receipt lands in the next test
        await ExecAsync("""
            TRUNCATE messaging.outbox, messaging.inbox, messaging.events, messaging.event_links, messaging.subscriptions, messaging.topology_versions, messaging.subscription_lanes, messaging.control_audit,
                     processing.runs, processing.generations, processing.stage_results, processing.attempts, processing.deliveries, processing.quarantine,
                     processing.observations, processing.extractions, llm_requests, incident_revisions, incident_observations, incidents,
                     collector_states, targets, target_tracks, track_targets, target_track_revisions, target_links, air_alerts, processing_errors, raw_messages RESTART IDENTITY CASCADE
            """);
        await ExecAsync("TRUNCATE analytics.message_lifecycle"); // P15 projection (analytics schema)
        Registrar.Reset();
        Outbox.Runs.Reset();
        Archive.ResetCounters();
        RawWriter.ResetCounters();
        Normalizer.ResetCounters();
        Parser.ResetCounters();
        LlmWorker.ResetCounters();
        Finalizer.ResetCounters();
        TrackWorker.ResetCounters();
        AlertWorker.ResetCounters();
        IncidentWorker.ResetCounters();
        Projection.ResetCounters();
        MessageAnalytics.ResetCounters();
        Watchdog.ResetMemo();
        Llm.Reset();
        Services.GetRequiredService<LlmBreaker>().Reset(); // a 429 in one test must not pause the model for the next
        await Registrar.EnsureRegisteredAsync(CancellationToken.None);
        await Declarer.DeclareAsync(CancellationToken.None);
        await PurgeQueuesAsync();
        await MessageAnalytics.StartAsync(CancellationToken.None);
    }

    public async Task PurgeQueuesAsync()
    {
        var connection = await Broker.GetAsync(CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync();
        foreach (var subscription in new[] { ArchiveHandler.Subscription, RawWriterHandler.Subscription, NormalizerHandler.Subscription, ParserHandler.Subscription, FinalizerHandler.Subscription, LlmWorkerHandler.Subscription, Puluj.Processing.Writers.TrackWriterHandler.Subscription, Puluj.Processing.Writers.AlertWriterHandler.Subscription, Puluj.Processing.Incidents.IncidentWriterHandler.Subscription, Puluj.Processing.Projection.ProjectionHandler.Subscription, Puluj.Processing.Analytics.MessageAnalyticsHandler.Subscription })
        {
            foreach (var lane in Registry.Subscription(subscription).Lanes)
            {
                await channel.QueuePurgeAsync(Registry.QueueName(subscription, lane));
                await channel.QueuePurgeAsync(Registry.DlqName(subscription, lane));
            }
        }
    }

    public IncomingMessage Message(string sourceMessageId, string? text, DateTimeOffset? publishedAt = null, int? sourceId = null) => new()
    {
        SourceId = sourceId ?? SourceId,
        SourceMessageId = sourceMessageId,
        PublishedAt = publishedAt ?? DateTimeOffset.UtcNow.AddSeconds(-5),
        RawText = text,
        Url = $"https://t.me/{SourceCode}/{sourceMessageId}",
    };

    /// <summary>Direct store (RawMessageIngestor with the P03 bridge): raw row + raw.stored outbox in one transaction.</summary>
    public Task<IngestResult> IngestAsync(string sourceMessageId, string text, bool enqueue = true, DateTimeOffset? publishedAt = null) =>
        Ingestor.IngestAsync(Message(sourceMessageId, text, publishedAt), SourceCode, CancellationToken.None, enqueue);

    /// <summary>The collectors' path (P04): ingress.received + checkpoint in one transaction; the raw-writer stores the row.</summary>
    public async Task<Source> SourceAsync() => await SourceAsync(SourceCode);

    /// <summary>A second, independent source (P09: duplicates across sources raise confidence).</summary>
    public const string SourceCode2 = "tg_kyiv_ova";

    public async Task<Source> SourceAsync(string code)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Sources.AsNoTracking().SingleAsync(s => s.Code == code);
    }

    public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return default!;
        }
        if (result is T typed)
        {
            return typed;
        }
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<long> CountAsync(string table, string where = "true") => ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {where}");

    public Task<long> CountAsync(string table, string where, params (string Name, object Value)[] parameters) => ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {where}", parameters);

    /// <summary>Ready + unacked messages of a queue (passive declare answers the ready count; consumers as well).</summary>
    public async Task<(uint Messages, uint Consumers)> QueueAsync(string queue)
    {
        var connection = await Broker.GetAsync(CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync();
        var ok = await channel.QueueDeclarePassiveAsync(queue);
        return (ok.MessageCount, ok.ConsumerCount);
    }

    public async Task PublishRawAsync(string routingKey, byte[] body, string messageId, string? exchange = null)
    {
        var connection = await Broker.GetAsync(CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await channel.BasicPublishAsync(exchange ?? Registry.ExchangeName, routingKey, mandatory: true,
            new BasicProperties { Persistent = true, MessageId = messageId, ContentType = "application/json" }, body);
    }

    /// <summary>A second replica of the archive subscription, or a consumer with a test handler on the archive queues.</summary>
    public SubscriptionConsumer NewConsumer(IDeliveryHandler? handler = null, string worker = "archive@replica-2") =>
        ActivatorUtilities.CreateInstance<SubscriptionConsumer>(Services, handler ?? Services.GetRequiredService<ArchiveHandler>(), worker);

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, int pollMs = 100)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(pollMs);
        }
        return await condition();
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Puluj.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public static string EvidencePath(string file)
    {
        var directory = Environment.GetEnvironmentVariable("PULUJ_TEST_EVIDENCE_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepoRoot(), "docs", "evidence", "message-platform")
            : directory;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, file);
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Puluj.Messaging.Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class MessagingCollection : ICollectionFixture<MessagingFixture>
{
    public const string Name = "messaging";
}

/// <summary>
/// Committed outcomes of messaging integration tests. A filtered run must merge its entries into the historical
/// evidence file: release gates intentionally execute only a subset and must never erase evidence from other tasks.
/// </summary>
public sealed class Evidence
{
    private readonly SortedDictionary<string, object> _entries = new(StringComparer.Ordinal);

    public void Record(string test, object values)
    {
        lock (_entries)
        {
            _entries[test] = values;
        }
    }

    public void Flush(string path, string brokerVersion)
    {
        lock (_entries)
        {
            if (_entries.Count == 0)
            {
                return;
            }
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            var tests = root["tests"]?.AsObject() ?? new JsonObject();
            foreach (var (name, values) in _entries)
            {
                tests[name] = JsonSerializer.SerializeToNode(values);
            }

            // Keep existing historical entries and their original environment context. `last_run` identifies exactly
            // which Testcontainers environment produced the entries merged by this invocation.
            root["task"] ??= "P03+P04";
            root["tests"] = tests;
            root["last_run"] = JsonSerializer.SerializeToNode(new
            {
                generated_at = DateTimeOffset.UtcNow,
                environment = new { broker_image = MessagingFixture.BrokerImage, broker_version = brokerVersion, postgres_image = "postgis/postgis:17-3.5", os = Environment.OSVersion.ToString(), dotnet = Environment.Version.ToString() },
                merged_tests = _entries.Keys.ToArray(),
            });
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
    }
}
