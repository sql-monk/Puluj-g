using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Puluj.Infrastructure.Messaging;

/// <summary>Holds a dedicated connection with LISTEN puluj_events and exposes the events as an async stream.</summary>
public sealed class PgNotifyListener(IConfiguration configuration, ILogger<PgNotifyListener> logger)
{
    private readonly string _connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName)
        ?? throw new InvalidOperationException("Connection string is not configured.");

    /// <summary>
    /// P11 (ADR-0011): NOTIFY is at-most-once — whatever was published while the LISTEN connection was down is gone. The
    /// pump yields this marker whenever a connection is (re)established after the first one, so the bridge can tell its
    /// clients to reload their window (`Resync`). Id = the connection generation.
    /// </summary>
    public static PulujEvent Reconnected(int generation, DateTimeOffset at) => new(PulujEventType.ListenerReconnected, generation, at);

    public async IAsyncEnumerable<PulujEvent> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<PulujEvent>(new UnboundedChannelOptions { SingleReader = true });

        _ = Task.Run(() => PumpAsync(channel.Writer, ct), ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }

    private async Task PumpAsync(ChannelWriter<PulujEvent> writer, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var generation = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<PulujEvent>(e.Payload);
                        if (evt is not null)
                        {
                            writer.TryWrite(evt);
                        }
                    }
                    catch (JsonException ex)
                    {
                        // An event type this build does not know (a newer worker during a rolling deploy) is not an error worth a warning per event.
                        logger.LogDebug(ex, "Unreadable NOTIFY payload skipped: {Payload}", e.Payload);
                    }
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {PgNotifyPublisher.Channel}", conn))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                logger.LogInformation("Listening on {Channel}", PgNotifyPublisher.Channel);
                delay = TimeSpan.FromSeconds(1);
                if (generation++ > 0)
                {
                    writer.TryWrite(Reconnected(generation, DateTimeOffset.UtcNow)); // notifications may have been missed: the clients must reload
                }
                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LISTEN connection lost; reconnecting in {Delay}", delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
        writer.TryComplete();
    }
}
