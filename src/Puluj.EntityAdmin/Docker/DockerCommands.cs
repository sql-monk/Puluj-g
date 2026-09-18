namespace Puluj.EntityAdmin.Docker;

/// <summary>
/// Argument lists of every docker command the panel runs, as pure functions (no shell, no quoting: each element is one
/// argv entry). Kept apart from <see cref="DockerService"/> so the exact commands are unit-tested.
/// </summary>
public static class DockerCommands
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string NumberLabel = "com.docker.compose.container-number";

    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal) { "restart", "stop", "start" };

    /// <summary>Probe: prints the daemon version, so it fails when the socket is missing (not only when the CLI is).</summary>
    public static string[] Version() => ["version", "--format", "{{.Server.Version}}"];

    /// <summary>Every container of the compose project, running or not, one JSON object per line.</summary>
    public static string[] Ps(string project) => ["ps", "-a", "--filter", $"label={ProjectLabel}={project}", "--format", "{{json .}}"];

    /// <summary>One sample of CPU / memory for every running container (about two seconds).</summary>
    public static string[] Stats() => ["stats", "--no-stream", "--format", "{{json .}}"];

    /// <summary>`docker restart|stop|start &lt;id&gt;`; the verb must be one of <see cref="Actions"/>.</summary>
    public static string[] Action(string containerId, string verb)
    {
        if (!Actions.Contains(verb))
        {
            throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown container action");
        }
        return [verb, containerId];
    }

    /// <summary>
    /// `docker compose up --scale service=N service`: --no-deps leaves postgis / migrate alone, --no-recreate keeps the
    /// replicas that already run even if the mounted compose file differs from the one the stack was started with,
    /// --no-build never rebuilds an image from the panel. Compose stops and removes the surplus replicas itself.
    /// </summary>
    public static string[] Scale(string project, string composeDir, IReadOnlyList<string> composeFiles, string? envFile, string service, int replicas)
    {
        var args = new List<string> { "compose", "-p", project, "--project-directory", composeDir };
        foreach (var file in composeFiles)
        {
            args.Add("-f");
            args.Add(Path.IsPathRooted(file) ? file : Path.Combine(composeDir, file));
        }
        if (envFile is not null)
        {
            args.Add("--env-file");
            args.Add(Path.IsPathRooted(envFile) ? envFile : Path.Combine(composeDir, envFile));
        }
        args.AddRange(["up", "-d", "--no-build", "--no-deps", "--no-recreate", "--scale", $"{service}={replicas}", service]);
        return [.. args];
    }

    /// <summary>
    /// The compose files to pass: the configured list, or docker-compose.yml plus docker-compose.override.yml when that
    /// file exists (the same rule compose applies on its own, made explicit because the project directory is a volume).
    /// </summary>
    public static IReadOnlyList<string> ComposeFiles(IReadOnlyList<string> configured, Func<string, bool> exists)
    {
        if (configured.Count > 0)
        {
            return configured;
        }
        var files = new List<string> { "docker-compose.yml" };
        if (exists("docker-compose.override.yml"))
        {
            files.Add("docker-compose.override.yml");
        }
        return files;
    }
}
