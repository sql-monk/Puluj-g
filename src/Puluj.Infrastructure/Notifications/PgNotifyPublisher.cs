using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Notifications;

/// <summary>Publishes events via PostgreSQL NOTIFY. Worker to Api bridge without extra infrastructure.</summary>
public sealed class PgNotifyPublisher(IDbContextFactory<PulujDbContext> factory, ILogger<PgNotifyPublisher> logger) : INotifyPublisher
{
    public const string Channel = "puluj_events";

    public async Task PublishAsync(PulujEvent evt, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var payload = JsonSerializer.Serialize(evt);
            await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, {payload})", ct);
        }
        catch (Exception ex)
        {
            // Realtime push is best-effort; the database remains the source of truth.
            logger.LogWarning(ex, "NOTIFY failed for {Type} {Id}", evt.Type, evt.Id);
        }
    }
}
