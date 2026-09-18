using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Processing.Pipeline;

/// <summary>
/// Holds one PostgreSQL session-level advisory lock for the lifetime of the processor.
/// This is the runtime backstop behind the non-scalable Compose service.
/// </summary>
public sealed class ProcessorSingletonLock(
    IDbContextFactory<PulujDbContext> factory,
    ILogger<ProcessorSingletonLock> logger)
{
    // Stable two-key namespace: "Puluj" / "processor". These are application constants, not row ids.
    private const int ApplicationLockKey = 1347767381;
    private const int ProcessorLockKey = 1886547823;

    public async Task<Lease> AcquireAsync(string instance, CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(@application, @processor)", connection);
            command.Parameters.AddWithValue("application", ApplicationLockKey);
            command.Parameters.AddWithValue("processor", ProcessorLockKey);
            var acquired = await command.ExecuteScalarAsync(ct) is true;
            if (!acquired)
            {
                throw new InvalidOperationException(
                    $"Another processor instance already owns the database lock; refusing to start '{instance}'.");
            }

            logger.LogInformation("Processor singleton lock acquired by {Instance}", instance);
            return new Lease(db, connection, instance, logger);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public sealed class Lease : IAsyncDisposable
    {
        private readonly PulujDbContext _db;
        private readonly NpgsqlConnection _connection;
        private readonly string _instance;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lost = new();
        private readonly Task _monitor;
        private bool _disposing;

        internal Lease(PulujDbContext db, NpgsqlConnection connection, string instance, ILogger logger)
        {
            _db = db;
            _connection = connection;
            _instance = instance;
            _logger = logger;
            _connection.StateChange += ConnectionStateChanged;
            _monitor = MonitorAsync();
        }

        public CancellationToken Lost => _lost.Token;

        private void ConnectionStateChanged(object? sender, System.Data.StateChangeEventArgs args)
        {
            if (!_disposing && args.CurrentState is System.Data.ConnectionState.Broken or System.Data.ConnectionState.Closed)
            {
                _logger.LogCritical("Processor singleton lock connection was lost by {Instance}", _instance);
                _lost.Cancel();
            }
        }

        private async Task MonitorAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(_lost.Token))
                {
                    await using var command = new NpgsqlCommand("SELECT 1", _connection);
                    await command.ExecuteScalarAsync(_lost.Token);
                }
            }
            catch (OperationCanceledException) when (_lost.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Processor singleton lock health check failed for {Instance}", _instance);
                _lost.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _disposing = true;
            _connection.StateChange -= ConnectionStateChanged;
            _lost.Cancel();
            await _monitor;
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var command = new NpgsqlCommand(
                        "SELECT pg_advisory_unlock(@application, @processor)", _connection);
                    command.Parameters.AddWithValue("application", ApplicationLockKey);
                    command.Parameters.AddWithValue("processor", ProcessorLockKey);
                    await command.ExecuteScalarAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not explicitly release processor singleton lock for {Instance}", _instance);
                }
            }
            await _db.DisposeAsync();
            _lost.Dispose();
        }
    }
}
