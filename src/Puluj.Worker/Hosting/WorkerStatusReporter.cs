using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Puluj.Contracts;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Settings;
using Puluj.Processing;
using Puluj.Processing.Llm;
using Puluj.Processing.Pipeline;

namespace Puluj.Worker.Hosting;

/// <summary>
/// Writes Runtime:Worker:{Name}:Status every 10 s: a WorkerStatusDto (JSON) with the build, the roles, process figures
/// (CPU, memory, threads) and, for an instance with the processing role, the ProcessingStats snapshot and the LLM
/// breaker state. The heartbeat key stays the liveness signal; this one is what the admin panel's "Workers" view shows.
/// Removed on a clean shutdown together with the heartbeat.
/// </summary>
public sealed class WorkerStatusReporter(
    SettingsStore settings,
    IOptions<WorkerOptions> options,
    IOptions<ProcessingOptions> processing,
    IOptionsMonitor<LlmOptions> llm,
    ReprocessService reprocess,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<WorkerStatusReporter> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProcessingStats? _stats = services.GetService<ProcessingStats>();
    private readonly LlmBreaker? _breaker = services.GetService<LlmBreaker>();
    private readonly string _version = BuildVersion(typeof(WorkerStatusReporter).Assembly);
    private readonly DateTimeOffset _builtAt = BuiltAt(typeof(WorkerStatusReporter).Assembly);
    private readonly DateTimeOffset _startedAt = StartedAt();
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.MinValue;

    private string StatusKey => $"Worker:{options.Value.InstanceName}:Status";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var status = await BuildAsync(ct);
                await settings.SetStatusAsync(StatusKey, JsonSerializer.Serialize(status, Json), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Status write failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await settings.SetStatusAsync(StatusKey, null, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Status removal failed");
        }
    }

    private async Task<WorkerStatusDto> BuildAsync(CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        var now = clock.GetUtcNow();
        var paused = await reprocess.PausedAsync(ct);
        // The hold itself is global, while the Telegram collector publishes the useful context: its current channel
        // or a concrete error. Include both in every Worker status so the processors panel is self-explanatory.
        var pause = paused is null ? null : new ProcessingPauseDto(
            paused,
            await settings.GetAsync("Runtime:Telegram:Status", ct));
        var llmOptions = llm.CurrentValue;
        return new WorkerStatusDto(
            options.Value.InstanceName,
            Environment.MachineName,
            options.Value.RoleSet.Order().ToList(),
            _version,
            _builtAt,
            _startedAt,
            now,
            Environment.ProcessId,
            process.WorkingSet64,
            CpuPercent(process),
            process.Threads.Count,
            _stats?.Snapshot(Math.Max(1, processing.Value.Concurrency)),
            _breaker is null ? null : new LlmStatusDto(
                llmOptions.Enabled,
                llmOptions.Model,
                _breaker.PausedUntil is { } until && until > now ? until : null,
                _breaker.PauseReason,
                _breaker.Calls,
                _breaker.Failures),
            paused,
            pause,
            [],
            null);
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
    internal static string BuildVersion(Assembly assembly)
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
