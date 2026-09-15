using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// The open run of a lane (ADR-0005): P03 keeps exactly one `running` run per lane `live` and `history` (partial unique
/// index `ux_processing_runs_open_per_lane`), created on first use with the pipeline version pinned. Replay runs,
/// state transitions and generations are P14. The id is cached per process; a superseded run (P14) would be picked up
/// after a restart, which is the moment a new build starts anyway.
/// </summary>
public sealed class ProcessingRuns(string pipelineVersion, string createdBy)
{
    private readonly ConcurrentDictionary<string, Guid> _open = new(StringComparer.Ordinal);

    public string PipelineVersion { get; } = pipelineVersion;

    public async Task<Guid> GetOpenRunAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string lane, CancellationToken ct)
    {
        if (_open.TryGetValue(lane, out var cached))
        {
            return cached;
        }
        var runId = await SelectOpenAsync(conn, tx, lane, ct);
        if (runId is null)
        {
            // Two processes may race here; the partial unique index lets exactly one insert win, the other re-selects.
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO processing.runs (run_id, lane, kind, state, versions, created_by, created_at, updated_at)
                VALUES (@run_id, @lane, @lane, 'running', @versions, @created_by, now(), now())
                ON CONFLICT DO NOTHING
                """, conn, tx);
            insert.Parameters.AddWithValue("run_id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("lane", lane);
            insert.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = $$"""{"pipeline_version":{{System.Text.Json.JsonSerializer.Serialize(PipelineVersion)}}}""" });
            insert.Parameters.AddWithValue("created_by", createdBy);
            await insert.ExecuteNonQueryAsync(ct);
            runId = await SelectOpenAsync(conn, tx, lane, ct) ?? throw new InvalidOperationException($"processing.runs: no open run for lane {lane} after insert");
        }
        _open[lane] = runId.Value;
        return runId.Value;
    }

    /// <summary>Forgets the cached ids (tests truncate the table; P14 supersedes runs).</summary>
    public void Reset() => _open.Clear();

    private static async Task<Guid?> SelectOpenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string lane, CancellationToken ct)
    {
        await using var select = new NpgsqlCommand("SELECT run_id FROM processing.runs WHERE lane = @lane AND state = 'running' AND kind IN ('live', 'history') LIMIT 1", conn, tx);
        select.Parameters.AddWithValue("lane", lane);
        return await select.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
}
