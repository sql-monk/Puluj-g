using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Puluj.Contracts;

namespace Puluj.Admin.Docker;

/// <summary>Outcome of a container action for the endpoint: the HTTP status to answer with and the body.</summary>
public sealed record ActionOutcome(int StatusCode, ContainerActionResultDto Result);

/// <summary>
/// Runs the docker CLI against the compose stack the panel itself runs in (docs/plan-admin-ops.md §2.3). Every
/// operation is limited to containers labelled with the compose project; the protected services are refused before
/// any process starts. The list (`ps` + `stats`, about two seconds) is cached for five seconds; the availability probe
/// (`docker version`) for a minute. Outside Docker (`Docker:Enabled=false`) everything reports "unavailable".
/// </summary>
public sealed class DockerService(IOptions<DockerOptions> options, ILogger<DockerService> log, TimeProvider clock)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60); // compose scale pulls nothing but may wait on health checks
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(1);
    private const int OutputLimit = 4096;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, string? Reason)? _probe;
    private (DateTimeOffset At, ContainersDto List)? _list;

    private DockerOptions Options => options.Value;
    public bool Enabled => Options.Enabled;
    private IReadOnlySet<string> Protected => new HashSet<string>(Options.ProtectedServices, StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the CLI answers; otherwise the reason shown in the panel.</summary>
    public async Task<string?> UnavailableAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return "керування контейнерами недоступне: панель запущена не в Docker (Docker:Enabled=false)";
        }
        var now = clock.GetUtcNow();
        if (_probe is { } p && now - p.At < ProbeTtl)
        {
            return p.Reason;
        }
        var res = await RunAsync(DockerCommands.Version(), ct);
        var reason = res.Ok ? null : $"docker недоступний: {FirstLine(res.Error ?? res.Stderr)}";
        if (reason is not null)
        {
            log.LogWarning("Docker: unavailable: {Reason}", reason);
        }
        _probe = (now, reason);
        return reason;
    }

    /// <summary>Containers of the project with a CPU / memory sample; cached, unless <paramref name="fresh"/>.</summary>
    public async Task<ContainersDto> ListAsync(CancellationToken ct, bool fresh = false)
    {
        if (await UnavailableAsync(ct) is { } reason)
        {
            return new ContainersDto(false, reason, Options.Project, [], 0);
        }
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (!fresh && _list is { } cached && now - cached.At < ListTtl)
            {
                return cached.List;
            }
            var psTask = RunAsync(DockerCommands.Ps(Options.Project), ct);
            var statsTask = RunAsync(DockerCommands.Stats(), ct);
            var ps = await psTask;
            var stats = await statsTask;
            if (!ps.Ok)
            {
                _probe = null; // let the next call probe again: the daemon may have gone away
                return new ContainersDto(false, $"docker ps: {FirstLine(ps.Error ?? ps.Stderr)}", Options.Project, [], 0);
            }
            var containers = DockerContainers.Build(
                DockerJson.ParseLines(ps.Stdout, DockerJson.ParsePs),
                stats.Ok ? DockerJson.ParseLines(stats.Stdout, DockerJson.ParseStats) : [],
                Protected);
            var replicas = containers.Count(c => c.Service == Options.ScalableService && c.State == "running");
            var list = new ContainersDto(true, null, Options.Project, containers, replicas);
            _list = (now, list);
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>restart | stop | start of one container of the project; 403 for a protected service, 404 for a foreign id, 502 when docker fails.</summary>
    public async Task<ActionOutcome> ActAsync(string containerId, string verb, string? remoteIp, CancellationToken ct)
    {
        if (!DockerCommands.Actions.Contains(verb))
        {
            return new ActionOutcome(400, new ContainerActionResultDto(false, $"невідома дія '{verb}'", ""));
        }
        var list = await ListAsync(ct, fresh: true);
        if (!list.Available)
        {
            return new ActionOutcome(503, new ContainerActionResultDto(false, list.Unavailable ?? "недоступно", ""));
        }
        var container = list.Containers.FirstOrDefault(c => c.Id.Equals(containerId, StringComparison.OrdinalIgnoreCase) || c.Name == containerId);
        if (container is null)
        {
            return new ActionOutcome(404, new ContainerActionResultDto(false, $"контейнер '{containerId}' не належить проєкту {Options.Project}", ""));
        }
        if (!container.Controllable)
        {
            log.LogWarning("Docker: {Action} {Target} by {RemoteIp} refused: protected service {Service}", verb, container.Name, remoteIp, container.Service);
            return new ActionOutcome(403, new ContainerActionResultDto(false, $"сервіс '{container.Service}' керується лише з консолі", ""));
        }
        log.LogInformation("Docker: {Action} {Target} by {RemoteIp}", verb, container.Name, remoteIp);
        var res = await RunAsync(DockerCommands.Action(container.Id, verb), ct);
        _list = null;
        return Outcome(res, $"{verb} {container.Name}");
    }

    /// <summary>`docker compose up --scale processor=N`; 0…MaxReplicas, otherwise 400.</summary>
    public Task<ActionOutcome> ScaleAsync(int replicas, string? remoteIp, CancellationToken ct) => ScaleAsync(Options.ScalableService, replicas, remoteIp, ct);

    /// <summary>`docker compose up --scale &lt;service&gt;=N` for one of <see cref="DockerOptions.ScalableServices"/> (P13: processor | messaging); 0…MaxReplicas, otherwise 400.</summary>
    public async Task<ActionOutcome> ScaleAsync(string service, int replicas, string? remoteIp, CancellationToken ct)
    {
        if (!Options.ScalableServices.Contains(service, StringComparer.Ordinal) && !string.Equals(service, Options.ScalableService, StringComparison.Ordinal))
        {
            return new ActionOutcome(400, new ContainerActionResultDto(false, $"сервіс '{service}' не масштабується з панелі", ""));
        }
        if (replicas < 0 || replicas > Options.MaxReplicas)
        {
            return new ActionOutcome(400, new ContainerActionResultDto(false, $"кількість реплік має бути від 0 до {Options.MaxReplicas}", ""));
        }
        if (await UnavailableAsync(ct) is { } reason)
        {
            return new ActionOutcome(503, new ContainerActionResultDto(false, reason, ""));
        }
        var dir = Options.ComposeDir;
        var files = DockerCommands.ComposeFiles(Options.ComposeFiles, f => File.Exists(Path.Combine(dir, f)));
        var envFile = File.Exists(Path.Combine(dir, ".env")) ? ".env" : null;
        var target = $"{service}={replicas}";
        log.LogInformation("Docker: {Action} {Target} by {RemoteIp}", "scale", target, remoteIp);
        var res = await RunAsync(DockerCommands.Scale(Options.Project, dir, files, envFile, service, replicas), ct);
        _list = null;
        return Outcome(res, $"scale {target}");
    }

    private ActionOutcome Outcome(CommandResult res, string what)
    {
        // compose reports progress on stderr even on success, so both streams go into the output.
        var output = Truncate(string.Join('\n', new[] { res.Stdout, res.Stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim());
        if (res.Ok)
        {
            log.LogInformation("Docker: {What} ok", what);
            return new ActionOutcome(200, new ContainerActionResultDto(true, $"{what}: виконано", output));
        }
        log.LogWarning("Docker: {What} failed (exit {ExitCode}): {Error}", what, res.ExitCode, FirstLine(res.Error ?? res.Stderr));
        return new ActionOutcome(502, new ContainerActionResultDto(false, $"{what}: помилка (код {res.ExitCode}): {FirstLine(res.Error ?? res.Stderr)}", output));
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr, string? Error = null)
    {
        public bool Ok => ExitCode == 0 && Error is null;
    }

    /// <summary>Runs `docker &lt;args&gt;` without a shell, kills it after the timeout, returns both streams.</summary>
    private async Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Options.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (Directory.Exists(Options.ComposeDir))
        {
            psi.WorkingDirectory = Options.ComposeDir;
        }
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new CommandResult(-1, "", "", $"{Options.Command}: {e.Message}");
        }
        if (process is null)
        {
            return new CommandResult(-1, "", "", $"{Options.Command}: процес не запустився");
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new CommandResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // already gone
                }
                ct.ThrowIfCancellationRequested();
                return new CommandResult(-1, "", "", $"docker {args[0]}: не відповів за {CommandTimeout.TotalSeconds:0} с");
            }
        }
    }

    private static string Truncate(string s) => s.Length <= OutputLimit ? s : s[..OutputLimit] + "\n… (обрізано)";

    private static string FirstLine(string s)
    {
        var t = s.Trim();
        var nl = t.IndexOf('\n');
        return nl < 0 ? t : t[..nl].Trim();
    }
}
