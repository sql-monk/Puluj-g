using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>
/// Один RabbitMQ (single node, management plugin) на test class. Restart/alarm змінюють стан брокера, тому
/// кожен клас має власний контейнер; дані quorum queue живуть у fs контейнера (docker stop/start зберігає).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:4.3-management";
    private const string User = "puluj";
    private const string Password = "puluj-spike";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image)
        .WithUsername(User)
        .WithPassword(Password)
        .WithPortBinding(15672, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    public Topology Topology { get; } = Topology.Load(Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "topology.json"));
    public string ServerVersion { get; private set; } = "";
    private HttpClient? _management;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ResetManagementClient();
        var overview = await _management!.GetFromJsonAsync<JsonObject>("api/overview");
        ServerVersion = overview!["rabbitmq_version"]!.GetValue<string>();
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await Topology.DeclareAsync(channel);
    }

    /// <summary>Після docker stop/start Testcontainers видає нові host-порти: клієнт management створюється заново.</summary>
    private void ResetManagementClient()
    {
        _management?.Dispose();
        _management = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}/") };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{User}:{Password}")));
    }

    public async Task DisposeAsync()
    {
        _management?.Dispose();
        await _container.DisposeAsync();
    }

    public ConnectionFactory Factory(bool automaticRecovery = false) => new()
    {
        HostName = _container.Hostname,
        Port = _container.GetMappedPublicPort(5672),
        UserName = User,
        Password = Password,
        AutomaticRecoveryEnabled = automaticRecovery,
        RequestedHeartbeat = TimeSpan.FromSeconds(5),
    };

    public Task<IConnection> ConnectAsync(bool automaticRecovery = false) => Factory(automaticRecovery).CreateConnectionAsync();

    /// <summary>docker stop + docker start того самого контейнера; чекає, поки брокер знову приймає підключення.</summary>
    public async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
        ResetManagementClient();
        await WaitForBrokerAsync();
    }

    public async Task WaitForBrokerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = await ConnectAsync();
                if (connection.IsOpen)
                {
                    return;
                }
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task<string> RabbitmqctlAsync(params string[] args)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", .. args]);
        return result.Stdout + result.Stderr;
    }

    /// <summary>Стан черги з management API: ready/unacked/total (+ redeliver з message_stats, якщо є).</summary>
    public async Task<QueueStats> QueueAsync(string queue)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await _management!.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}");
            if (response.IsSuccessStatusCode)
            {
                var q = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                return new QueueStats(
                    q["messages_ready"]?.GetValue<int>() ?? 0,
                    q["messages_unacknowledged"]?.GetValue<int>() ?? 0,
                    q["messages"]?.GetValue<int>() ?? 0,
                    q["message_stats"]?["redeliver"]?.GetValue<long>() ?? 0,
                    q["consumers"]?.GetValue<int>() ?? 0,
                    q["type"]?.GetValue<string>() ?? "");
            }
            if (attempt >= 10)
            {
                throw new InvalidOperationException($"management API {response.StatusCode} for {queue}");
            }
            await Task.Delay(300);
        }
    }

    /// <summary>Чекає, поки черга досягне очікуваного стану (management API оновлюється з затримкою ~5 s).</summary>
    public async Task<QueueStats> WaitQueueAsync(string queue, Func<QueueStats, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        QueueStats stats;
        do
        {
            stats = await QueueAsync(queue);
            if (predicate(stats))
            {
                return stats;
            }
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return stats;
    }

    public async Task<JsonObject> QueueRawAsync(string queue) =>
        (await _management!.GetFromJsonAsync<JsonObject>($"api/queues/%2F/{Uri.EscapeDataString(queue)}"))!;

    public async Task<JsonArray> BindingsAsync(string queue)
    {
        var bindings = await _management!.GetFromJsonAsync<JsonArray>($"api/queues/%2F/{Uri.EscapeDataString(queue)}/bindings");
        return bindings!;
    }

    public async Task<bool> MemoryAlarmActiveAsync()
    {
        var nodes = await _management!.GetFromJsonAsync<JsonArray>("api/nodes");
        return nodes![0]!["mem_alarm"]!.GetValue<bool>();
    }
}

public sealed record QueueStats(int Ready, int Unacked, int Total, long Redelivered, int Consumers, string Type);
