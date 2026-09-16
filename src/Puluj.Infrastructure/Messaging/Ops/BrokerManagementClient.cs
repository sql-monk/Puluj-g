using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Contracts;

namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// Optional second source of queue figures (ADR-0012): RabbitMQ's management API (`Messaging:Broker:ManagementUrl`,
/// user/password default to the AMQP credentials; the user needs the `management` tag). Unreachable or not configured
/// → <see cref="Snapshot.Available"/> false with the reason, and the panel shows the database view only. Never
/// invents a number: nothing here is cached beyond one snapshot.
/// </summary>
public sealed class BrokerManagementClient(IOptions<MessagingOptions> options, ILogger<BrokerManagementClient> logger) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http = new() { Timeout = Timeout };

    public sealed record Queue(string Name, long Ready, long Unacked, int Consumers);
    public sealed record Snapshot(bool Available, string? Reason, IReadOnlyDictionary<string, Queue> Queues, IReadOnlyList<BrokerNodeDto> Nodes);

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.Broker.ManagementUrl);

    public async Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        var broker = options.Value.Broker;
        if (!Configured)
        {
            return new Snapshot(false, "Messaging:Broker:ManagementUrl не задано", new Dictionary<string, Queue>(), []);
        }
        var baseUrl = broker.ManagementUrl!.TrimEnd('/');
        var vhost = Uri.EscapeDataString(broker.VirtualHost);
        try
        {
            var queues = new Dictionary<string, Queue>(StringComparer.Ordinal);
            using (var doc = await GetAsync($"{baseUrl}/api/queues/{vhost}?columns=name,messages_ready,messages_unacknowledged,consumers", ct))
            {
                foreach (var q in doc.RootElement.EnumerateArray())
                {
                    var name = q.GetProperty("name").GetString() ?? "";
                    queues[name] = new Queue(name, Long(q, "messages_ready"), Long(q, "messages_unacknowledged"), (int)Long(q, "consumers"));
                }
            }
            var nodes = new List<BrokerNodeDto>();
            using (var doc = await GetAsync($"{baseUrl}/api/nodes?columns=name,running,mem_alarm,disk_free_alarm", ct))
            {
                foreach (var n in doc.RootElement.EnumerateArray())
                {
                    nodes.Add(new BrokerNodeDto(n.GetProperty("name").GetString() ?? "", Bool(n, "running"), Bool(n, "mem_alarm"), Bool(n, "disk_free_alarm")));
                }
            }
            return new Snapshot(true, null, queues, nodes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            logger.LogDebug(ex, "Broker management API unavailable");
            return new Snapshot(false, $"management API недоступний: {ex.Message}", new Dictionary<string, Queue>(), []);
        }
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        var broker = options.Value.Broker;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var user = broker.ManagementUser ?? broker.User;
        var password = broker.ManagementPassword ?? broker.Password;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
