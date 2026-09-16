namespace Puluj.Admin.Docker;

/// <summary>
/// Container management from the admin panel (`Docker:*`). Off by default: it only makes sense inside the compose
/// stack, where the Docker socket is mounted into the admin container and the CLI is in the image (deploy/Dockerfile.admin).
/// </summary>
public sealed class DockerOptions
{
    public const string Section = "Docker";

    public bool Enabled { get; set; }

    /// <summary>The docker CLI (with the compose plugin) the panel runs; the socket comes from the environment (DOCKER_HOST or /var/run/docker.sock).</summary>
    public string Command { get; set; } = "docker";

    /// <summary>Directory with the compose files and .env, as seen from this process (the compose volume `/deploy` in the stack).</summary>
    public string ComposeDir { get; set; } = "/deploy";

    /// <summary>Compose project name: only containers labelled `com.docker.compose.project=&lt;Project&gt;` are listed or touched.</summary>
    public string Project { get; set; } = "puluj-g";

    /// <summary>Compose files relative to ComposeDir; empty = docker-compose.yml plus docker-compose.override.yml if it exists.</summary>
    public List<string> ComposeFiles { get; set; } = [];

    /// <summary>The compose service that is scaled from the panel.</summary>
    public string ScalableService { get; set; } = "processor";

    /// <summary>Services the P13 messaging panel may scale (`--scale &lt;service&gt;=N`); `messaging` is under the `broker` profile — compose enables it for an explicitly named service.</summary>
    public List<string> ScalableServices { get; set; } = ["processor", "messaging"];

    public int MaxReplicas { get; set; } = 8;

    /// <summary>Services the panel never restarts or stops (view only): the database, the panel itself, the one-shot migrator.</summary>
    public List<string> ProtectedServices { get; set; } = ["admin", "postgis", "migrate"];
}
