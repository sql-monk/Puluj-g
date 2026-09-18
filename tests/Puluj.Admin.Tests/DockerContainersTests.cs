using Puluj.Admin.Docker;
using Puluj.Contracts;

namespace Puluj.Admin.Tests;

public class DockerContainersTests
{
    private static readonly HashSet<string> Protected = new(["admin", "postgis", "migrate"], StringComparer.OrdinalIgnoreCase);

    private static PsRow Ps(string id, string service, string name, string state = "running", string number = "1") =>
        new(id, name, "img", state, state == "running" ? "Up 1 hour" : "Exited (0) 1 hour ago",
            new Dictionary<string, string> { [DockerCommands.ProjectLabel] = "puluj", [DockerCommands.ServiceLabel] = service, [DockerCommands.NumberLabel] = number }, null);

    private static List<ContainerDto> Stack() => DockerContainers.Build(
        [
            Ps("aaa111", "postgis", "puluj-postgis-1"),
            Ps("616c2ab99756", "processor", "puluj-processor-1"),
            Ps("7f0d1e2c3b4a", "processor", "puluj-processor-2", number: "2"),
            Ps("bbb222", "admin", "puluj-admin-1"),
            Ps("ccc333", "collector-alerts", "puluj-collector-alerts-1"),
            Ps("ddd444", "migrate", "puluj-migrate-1", state: "exited"),
            Ps("eee555", "analytics", "puluj-analytics-1"),
        ],
        [new StatsRow("616c2ab99756", "puluj-processor-1", 12.5, 100_000_000, 8_000_000_000)],
        Protected);

    [Fact]
    public void Build_marks_protected_services_view_only_and_joins_stats_by_id()
    {
        var list = Stack();

        var processor = list.Single(c => c.Name == "puluj-processor-1");
        Assert.True(processor.Controllable);
        Assert.Equal(12.5, processor.CpuPercent);
        Assert.Equal(100_000_000, processor.MemoryBytes);
        Assert.Equal(1, processor.ReplicaNumber);
        Assert.Equal(2, list.Single(c => c.Name == "puluj-processor-2").ReplicaNumber);
        Assert.Null(list.Single(c => c.Name == "puluj-processor-2").CpuPercent);
        Assert.All(list.Where(c => c.Service is "admin" or "postgis" or "migrate"), c => Assert.False(c.Controllable));
        Assert.True(list.Single(c => c.Service == "collector-alerts").Controllable);
        Assert.True(list.Single(c => c.Service == "analytics").Controllable);
    }

    [Fact]
    public void Build_orders_processors_first_then_the_rest_by_role()
    {
        var services = Stack().Select(c => c.Service).ToList();

        Assert.Equal(["processor", "processor", "collector-alerts", "analytics", "admin", "postgis", "migrate"], services);
    }

    [Fact]
    public void Build_ignores_a_container_without_a_service_label()
    {
        var row = new PsRow("fff", "stray", "img", "running", "Up", new Dictionary<string, string>(), null);
        var list = DockerContainers.Build([row], [], Protected);

        Assert.Single(list);
        Assert.False(list[0].Controllable);
        Assert.Equal("", list[0].Service);
    }

    [Theory]
    [InlineData("processor-616c2ab99756", "processor")]
    [InlineData("processor", "processor")]
    [InlineData("collector-telegram", "collector-telegram")]
    [InlineData("collector-alerts", "collector-alerts")]
    [InlineData("analytics", "analytics")]
    [InlineData("worker", "worker")]
    [InlineData("worker-desktop", "worker")]
    [InlineData("migrate", "migrate")]
    [InlineData("something", "other")]
    public void KindOf_reads_the_role_from_the_instance_name(string name, string kind)
    {
        Assert.Equal(kind, DockerContainers.KindOf(name));
    }

    [Fact]
    public void Match_pins_a_replica_by_the_container_id_in_its_name()
    {
        var list = Stack();

        Assert.Equal("puluj-processor-2", DockerContainers.Match("processor-7f0d1e2c3b4a", "processor", list)?.Name);
        Assert.Equal("puluj-processor-1", DockerContainers.Match("processor-616C2AB99756", "processor", list)?.Name);
        Assert.Equal("puluj-processor-1", DockerContainers.Match("processor-000000000000", "processor", list)?.Name);
        Assert.Equal("puluj-processor-1", DockerContainers.Match("processor-desktop", "processor", list)?.Name);
        Assert.Equal("puluj-processor-1", DockerContainers.Match("processor", "processor", list)?.Name);
    }

    [Fact]
    public void Match_uses_the_service_for_single_container_roles()
    {
        var list = Stack();

        Assert.Equal("puluj-collector-alerts-1", DockerContainers.Match("collector-alerts", "collector-alerts", list)?.Name);
        Assert.Equal("puluj-analytics-1", DockerContainers.Match("analytics", "analytics", list)?.Name);
        Assert.Null(DockerContainers.Match("collector-telegram", "collector-telegram", list));
        Assert.Null(DockerContainers.Match("worker", "worker", list));
    }
}
