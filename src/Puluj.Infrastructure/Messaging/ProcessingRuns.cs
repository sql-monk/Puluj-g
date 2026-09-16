using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// The open run of a lane (ADR-0005): exactly one `running` run per lane `live` and `history` (partial unique index
/// `ux_processing_runs_open_per_lane`), created on first use with the pipeline version pinned. P14: a process with a newer
/// `pipeline_version` supersedes the open run (old → `superseded` with `finished_at`, new → `running` with `supersedes_run_id`,
/// one transaction; two processes racing — the unique index lets one insert win, the other re-selects). The id is cached per
/// process; an older build keeps using its cached run until restart (results stay attributed to the version that made them).
/// Replay runs are created and driven by <c>RunService</c>.
/// </summary>
public sealed class ProcessingRuns(string pipelineVersion, string createdBy, DateTimeOffset? startedAt = null)
{
    private readonly ConcurrentDictionary<string, Guid> _open = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = startedAt ?? DateTimeOffset.UtcNow;

    public string PipelineVersion { get; } = pipelineVersion;

    public async Task<Guid> GetOpenRunAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string lane, CancellationToken ct)
    {
        if (_open.TryGetValue(lane, out var cached))
        {
            return cached;
        }
        var open = await SelectOpenAsync(conn, tx, lane, ct);
        // A newer run opened after this process started is adopted, never superseded back (mixed-version window, review N1): the newest
        // deploy owns the lane; an older replica attributes its last results to the new run until it is restarted.
        if (open is { } stale && stale.PipelineVersion != PipelineVersion && stale.CreatedAt < _startedAt)
        {
            // A newer build: close the old run and open ours in one statement pair (same tx as the caller's, or autocommit per statement —
            // then the unique index still guarantees a single open run: a failed insert re-selects).
            await using var supersede = new NpgsqlCommand("UPDATE processing.runs SET state = 'superseded', finished_at = now(), updated_at = now() WHERE run_id = @id AND state = 'running'", conn, tx);
            supersede.Parameters.AddWithValue("id", stale.RunId);
            await supersede.ExecuteNonQueryAsync(ct);
            await InsertOpenAsync(conn, tx, lane, stale.RunId, ct);
            open = await SelectOpenAsync(conn, tx, lane, ct);
        }
        if (open is null)
        {
            // Two processes may race here; the partial unique index lets exactly one insert win, the other re-selects.
            await InsertOpenAsync(conn, tx, lane, null, ct);
            open = await SelectOpenAsync(conn, tx, lane, ct) ?? throw new InvalidOperationException($"processing.runs: no open run for lane {lane} after insert");
        }
        _open[lane] = open.Value.RunId;
        return open.Value.RunId;
    }

    private async Task InsertOpenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string lane, Guid? supersedes, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO processing.runs (run_id, lane, kind, state, versions, supersedes_run_id, created_by, created_at, updated_at)
            VALUES (@run_id, @lane, @lane, 'running', @versions, @supersedes, @created_by, now(), now())
            ON CONFLICT DO NOTHING
            """, conn, tx);
        insert.Parameters.AddWithValue("run_id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("lane", lane);
        insert.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = $$"""{"pipeline_version":{{System.Text.Json.JsonSerializer.Serialize(PipelineVersion)}}}""" });
        insert.Parameters.AddWithValue("supersedes", (object?)supersedes ?? DBNull.Value);
        insert.Parameters.AddWithValue("created_by", createdBy);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Forgets the cached ids (tests truncate the table; P14 supersedes runs).</summary>
    public void Reset() => _open.Clear();

    private static async Task<(Guid RunId, string? PipelineVersion, DateTimeOffset CreatedAt)?> SelectOpenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string lane, CancellationToken ct)
    {
        await using var select = new NpgsqlCommand("SELECT run_id, versions->>'pipeline_version', created_at FROM processing.runs WHERE lane = @lane AND state = 'running' AND kind IN ('live', 'history') LIMIT 1", conn, tx);
        select.Parameters.AddWithValue("lane", lane);
        await using var reader = await select.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)) : null;
    }
}
