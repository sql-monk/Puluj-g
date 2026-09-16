using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Puluj.Infrastructure.Persistence;

/// <summary>
/// P09: an EF context over a consumer's open connection and transaction, so the legacy sinks (EF code) run inside the
/// delivery transaction that also writes the inbox receipt and the outbox events. Same model options as the factory
/// (NetTopologySuite, snake_case); the context never opens or closes the connection and never commits.
/// </summary>
public static class ConsumerDbContext
{
    public static async Task<PulujDbContext> AttachAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<PulujDbContext>()
            .UseNpgsql(conn, npgsql => npgsql.UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new PulujDbContext(options);
        await db.Database.UseTransactionAsync(tx, ct);
        return db;
    }
}
