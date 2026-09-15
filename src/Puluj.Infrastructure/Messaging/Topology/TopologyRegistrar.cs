using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Messaging.Topology;

/// <summary>
/// Keeps `messaging.topology_versions` and `messaging.subscriptions` in step with the embedded `topology.json`
/// (ADR-0002 registry, ADR-0006). Idempotent and safe from any process: the migrate role runs it after the migrations,
/// and the outbox writer, relay and consumers call it lazily before their first use, so a collector that starts before
/// any broker role still records the expected deliveries of what it publishes. A different file under an already
/// registered version number is refused — bump `topology_version` instead.
/// Bindings/lanes/required always come from the file; `status` is inserted once and then owned by the database
/// (pause/waiver by operators, activation by a new version).
/// </summary>
public sealed class TopologyRegistrar(TopologyRegistry registry, IDbContextFactory<PulujDbContext> factory, ILogger<TopologyRegistrar> logger)
{
    private volatile bool _registered;

    public TopologyRegistry Registry { get; } = registry;

    public async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (_registered)
        {
            return;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await RegisterAsync(conn, null, ct);
    }

    /// <summary>
    /// Registration from inside a caller's transaction (outbox writer in the ingest transaction) runs on its own
    /// autocommit connection first: the registry rows must be durable even if the caller rolls back, and the caller's
    /// transaction (read committed) then sees them.
    /// </summary>
    public Task EnsureRegisteredAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct) =>
        _registered ? Task.CompletedTask : tx is null ? RegisterAsync(conn, null, ct) : EnsureRegisteredAsync(ct);

    private async Task RegisterAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using (var insertVersion = new NpgsqlCommand(
            "INSERT INTO messaging.topology_versions (topology_version, hash, applied_at, applied_by) VALUES (@v, @hash, now(), @by) ON CONFLICT DO NOTHING", conn, tx))
        {
            insertVersion.Parameters.AddWithValue("v", Registry.TopologyVersion);
            insertVersion.Parameters.AddWithValue("hash", Registry.Hash);
            insertVersion.Parameters.AddWithValue("by", Environment.MachineName);
            await insertVersion.ExecuteNonQueryAsync(ct);
        }
        await using (var selectHash = new NpgsqlCommand("SELECT hash FROM messaging.topology_versions WHERE topology_version = @v", conn, tx))
        {
            selectHash.Parameters.AddWithValue("v", Registry.TopologyVersion);
            var stored = (string?)await selectHash.ExecuteScalarAsync(ct);
            if (!string.Equals(stored, Registry.Hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"topology.json version {Registry.TopologyVersion} is already registered with hash {stored}, this build embeds {Registry.Hash}: bump topology_version (ADR-0002).");
            }
        }
        foreach (var subscription in Registry.Subscriptions.Values)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO messaging.subscriptions (subscription_id, topology_version, required, status, bindings, lanes, queue_policy, owner_task, activated_at, updated_at)
                VALUES (@id, @v, @required, @status, @bindings, @lanes, @policy, @owner, CASE WHEN @status = 'active' THEN now() END, now())
                ON CONFLICT DO NOTHING
                """, conn, tx);
            insert.Parameters.AddWithValue("id", subscription.Id);
            insert.Parameters.AddWithValue("v", Registry.TopologyVersion);
            insert.Parameters.AddWithValue("required", subscription.Required);
            insert.Parameters.AddWithValue("status", subscription.Status);
            insert.Parameters.Add(new NpgsqlParameter("bindings", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(subscription.Bindings) });
            insert.Parameters.Add(new NpgsqlParameter("lanes", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(subscription.Lanes) });
            insert.Parameters.AddWithValue("policy", subscription.QueuePolicy);
            insert.Parameters.AddWithValue("owner", (object?)subscription.OwnerTask ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
        _registered = true;
        logger.LogInformation("Topology version {Version} ({Hash}) registered: {Count} subscriptions", Registry.TopologyVersion, Registry.Hash[..12], Registry.Subscriptions.Count);
    }

    /// <summary>Database-owned status per subscription for the current topology version (empty until registered).</summary>
    public static async Task<Dictionary<string, string>> StatusesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int topologyVersion, CancellationToken ct)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var select = new NpgsqlCommand("SELECT subscription_id, status FROM messaging.subscriptions WHERE topology_version = @v", conn, tx);
        select.Parameters.AddWithValue("v", topologyVersion);
        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            statuses[reader.GetString(0)] = reader.GetString(1);
        }
        return statuses;
    }

    /// <summary>Forgets that registration happened (tests truncate the registry between cases).</summary>
    public void Reset() => _registered = false;
}
