using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace Puluj.Messaging;

/// <summary>
/// One RabbitMQ connection per process, opened on first use and re-opened after it is lost (the client's automatic
/// recovery covers short outages; a dead connection is replaced on the next call). Channels are per role — relay,
/// each consumer, declarer — and short-lived where a failure would poison them (publish returns, passive declares).
/// </summary>
public sealed class BrokerConnection(IOptions<MessagingOptions> options, ILogger<BrokerConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>True while the process holds an open connection (worker status `Broker`, P13); false before the first use and after a loss until the next call re-opens it.</summary>
    public bool IsConnected => _connection is { IsOpen: true };
    public string Endpoint => $"{options.Value.Broker.Host}:{options.Value.Broker.Port}{options.Value.Broker.VirtualHost}";

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        var current = _connection;
        if (current is { IsOpen: true })
        {
            return current;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }
            if (_connection is not null)
            {
                try
                {
                    await _connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Disposing a dead broker connection");
                }
            }
            var broker = options.Value.Broker;
            var factory = new ConnectionFactory
            {
                HostName = broker.Host,
                Port = broker.Port,
                VirtualHost = broker.VirtualHost,
                UserName = broker.User,
                Password = broker.Password,
                RequestedHeartbeat = broker.Heartbeat,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = broker.ClientName ?? $"puluj-{Environment.MachineName.ToLowerInvariant()}",
            };
            _connection = await factory.CreateConnectionAsync(ct);
            logger.LogInformation("Broker connection opened: {Host}:{Port}{VHost}", broker.Host, broker.Port, broker.VirtualHost);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the current connection so the next call opens a fresh one (tests simulate a process restart).</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
