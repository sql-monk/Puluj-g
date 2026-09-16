using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Analytics;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Lifecycle;
using Puluj.Analytics.Persistence;
using Puluj.Contracts;

namespace Puluj.Analytics.Worker;

/// <summary>Applies the `analytics` schema migrations before the loop starts. Only this service ever migrates the schema, so a plain advisory lock is enough against a restart race.</summary>
public sealed class AnalyticsInitializer(IDbContextFactory<AnalyticsDbContext> factory, ILogger<AnalyticsInitializer> logger) : IHostedService
{
    private const long LockKey = 0x414E414C594D; // "ANALYM"

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} analytics migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
                await db.Database.MigrateAsync(ct);
            }
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Runs the analysis on a timer: drain the backlog, sleep `Analytics:Interval`, repeat. Consecutive failures are counted for /health. P15: the same pass advances the lifecycle backfill and runs the reconciliation sweep.</summary>
public sealed class AnalysisLoop(AnalysisRunner runner, LifecycleBackfill backfill, LifecycleReconciliation reconciliation, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<AnalysisLoop> logger) : BackgroundService
{
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Analytics loop started (every {Interval})", options.Value.Interval);
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await runner.RunOnceAsync(ct);
                // P15 (ADR-0013): the lifecycle projection — a bounded backfill step (durable evidence → rows) and the reconciliation of the recent window.
                await backfill.RunAsync(options.Value.LifecycleBackfillBatch, options.Value.LifecycleBackfillBatchesPerPass, ct);
                await reconciliation.RunAsync(options.Value.LifecycleReconcileWindow, options.Value.LifecycleReconcileGrace, ct);
                ConsecutiveFailures = 0;
                LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                LastError = ex.Message;
                logger.LogError(ex, "Analytics run failed ({Failures} in a row)", ConsecutiveFailures);
            }
            LastRunAt = clock.GetUtcNow();
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>
/// Writes `Runtime:Worker:{Name}:Heartbeat` and `Runtime:Worker:{Name}:Status` (a WorkerStatusDto without the
/// processing part: build, uptime, process figures) into `app_settings` every 10 s — the same key family the Worker
/// instances use, so the admin panel lists this service next to them without knowing anything about it. Both keys are
/// removed on a clean stop.
/// </summary>
public sealed class AnalyticsHeartbeat(IDbContextFactory<AnalyticsDbContext> factory, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<AnalyticsHeartbeat> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _version = BuildVersion(typeof(AnalyticsHeartbeat).Assembly);
    private readonly DateTimeOffset _builtAt = BuiltAt(typeof(AnalyticsHeartbeat).Assembly);
    private readonly DateTimeOffset _startedAt = StartedAt();
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.MinValue;

    private string HeartbeatKey => $"Runtime:Worker:{options.Value.Name}:Heartbeat";
    private string StatusKey => $"Runtime:Worker:{options.Value.Name}:Status";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var now = clock.GetUtcNow();
                var status = JsonSerializer.Serialize(Status(now), Json);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO app_settings (key, value, is_secret, updated_at)
                    VALUES ({HeartbeatKey}, {now.ToString("O")}, false, {now}), ({StatusKey}, {status}, false, {now})
                    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
                    """, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Heartbeat write failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var db = await factory.CreateDbContextAsync(cts.Token);
            await db.Database.ExecuteSqlAsync($"DELETE FROM app_settings WHERE key IN ({HeartbeatKey}, {StatusKey})", cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Heartbeat removal failed");
        }
    }

    private WorkerStatusDto Status(DateTimeOffset now)
    {
        using var process = Process.GetCurrentProcess();
        return new WorkerStatusDto(
            options.Value.Name,
            Environment.MachineName,
            ["analytics"],
            _version,
            _builtAt,
            _startedAt,
            now,
            Environment.ProcessId,
            process.WorkingSet64,
            CpuPercent(process),
            process.Threads.Count,
            Processing: null,
            Llm: null,
            Paused: null,
            Pause: null);
    }

    /// <summary>Processor time used since the previous call over the wall time that passed, per core, in percent.</summary>
    private double CpuPercent(Process process)
    {
        var cpu = process.TotalProcessorTime;
        var at = DateTimeOffset.UtcNow;
        var wall = (at - _lastCpuAt).TotalMilliseconds * Environment.ProcessorCount;
        var percent = _lastCpuAt == DateTimeOffset.MinValue || wall <= 0 ? 0 : Math.Round(100.0 * (cpu - _lastCpu).TotalMilliseconds / wall, 1);
        _lastCpu = cpu;
        _lastCpuAt = at;
        return Math.Clamp(percent, 0, 100);
    }

    private static DateTimeOffset StartedAt()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>AssemblyInformationalVersion with the source revision cut to 12 characters ("1.0.0+1a2b3c4d5e6f").</summary>
    private static string BuildVersion(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length - plus - 1 > 12 ? version[..(plus + 13)] : version;
    }

    /// <summary>When the main assembly was written: tells replicas of different builds apart when the version is the same.</summary>
    private static DateTimeOffset BuiltAt(Assembly assembly)
    {
        var path = string.IsNullOrEmpty(assembly.Location) ? Environment.ProcessPath : assembly.Location;
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? DateTimeOffset.MinValue : new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }
}
