using Puluj.Contracts;

namespace Puluj.Admin.Docker;

/// <summary>
/// Pure half of the container view: folding `docker ps` and `docker stats` rows into <see cref="ContainerDto"/>s, and
/// matching a Worker instance (heartbeat name) to its container.
/// </summary>
public static class DockerContainers
{
    public static List<ContainerDto> Build(IEnumerable<PsRow> ps, IEnumerable<StatsRow> stats, IReadOnlySet<string> protectedServices)
    {
        var statsById = new Dictionary<string, StatsRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stats)
        {
            statsById[s.Id] = s;
        }
        return ps.Select(p =>
        {
            var service = p.Label(DockerCommands.ServiceLabel) ?? "";
            var st = statsById.GetValueOrDefault(p.Id) ?? statsById.Values.FirstOrDefault(s => s.Name == p.Name);
            var number = int.TryParse(p.Label(DockerCommands.NumberLabel), out var n) ? n : (int?)null;
            return new ContainerDto(p.Id, p.Name, service, p.Image, p.State, p.Status, p.CreatedAt,
                st?.CpuPercent, st?.MemoryBytes, st?.MemoryLimitBytes,
                service.Length > 0 && !protectedServices.Contains(service), number);
        })
        .OrderBy(c => ServiceOrder(c.Service)).ThenBy(c => c.ReplicaNumber ?? 0).ThenBy(c => c.Name, StringComparer.Ordinal)
        .ToList();
    }

    /// <summary>processor | collector-telegram | collector-alerts | analytics | worker | migrate | other — from the instance name.</summary>
    public static string KindOf(string instanceName)
    {
        foreach (var kind in new[] { "collector-telegram", "collector-alerts", "processor", "analytics", "migrate", "worker" })
        {
            if (instanceName.Equals(kind, StringComparison.OrdinalIgnoreCase) || instanceName.StartsWith(kind + "-", StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }
        return "other";
    }

    /// <summary>
    /// The container of an instance. A host-name suffix is matched against container ids first; every deployed worker
    /// service, including processor, otherwise has exactly one container and is matched by its compose service name.
    /// </summary>
    public static ContainerDto? Match(string instanceName, string kind, IReadOnlyList<ContainerDto> containers)
    {
        var dash = instanceName.LastIndexOf('-');
        if (dash > 0)
        {
            var suffix = instanceName[(dash + 1)..];
            if (suffix.Length == 12 && suffix.All(Uri.IsHexDigit))
            {
                var byId = containers.FirstOrDefault(c => c.Id.StartsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (byId is not null)
                {
                    return byId;
                }
            }
        }
        if (kind is "other" or "worker")
        {
            return null;
        }
        return containers.Where(c => c.Service.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.State == "running").FirstOrDefault();
    }

    private static int ServiceOrder(string service) => service switch
    {
        "processor" => 0,
        "collector-telegram" => 1,
        "collector-alerts" => 2,
        "analytics" => 3,
        "api" => 4,
        "admin" => 5,
        "postgis" => 6,
        "migrate" => 7,
        _ => 8,
    };
}
