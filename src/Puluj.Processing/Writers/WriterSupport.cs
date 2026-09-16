using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Stages;

namespace Puluj.Processing.Writers;

/// <summary>
/// P09 shared pieces of the domain writers (track-worker, alert-worker): the lock hierarchy, the double-writer guards,
/// the `targets` rows keyed by observation, aggregate revisions and the change events (ADR-0009).
/// </summary>
public static class WriterSupport
{
    public const string TrackScope = "track";
    public const string SchemaVersion = "1.0";

    /// <summary>Store, shared: excludes the legacy processor/watchdog/reset (exclusive Store) while the writers stay parallel among themselves (review B7).</summary>
    public static async Task<long> LockStoreSharedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(@k)", conn, tx);
        cmd.Parameters.AddWithValue("k", AdvisoryLocks.Store);
        await cmd.ExecuteNonQueryAsync(ct);
        return sw.ElapsedMilliseconds;
    }

    public static async Task<long> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, bool shared, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = new NpgsqlCommand(shared ? "SELECT pg_advisory_xact_lock_shared(hashtext(@k))" : "SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync(ct);
        return sw.ElapsedMilliseconds;
    }

    /// <summary>The legacy loop wrote this raw (rows without an observation): the writers must not write it again (review B7).</summary>
    public static async Task<bool> LegacyOwnedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM targets WHERE raw_message_id = @raw AND observation_id IS NULL)", conn, tx);
        cmd.Parameters.AddWithValue("raw", rawId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Observations of this raw already materialized — by this delivery's set (a republished event) or by another set (another run: history reload, review B6).</summary>
    public static async Task<HashSet<Guid>> WrittenObservationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT observation_id FROM targets WHERE raw_message_id = @raw AND observation_id IS NOT NULL", conn, tx);
        cmd.Parameters.AddWithValue("raw", rawId);
        var set = new HashSet<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            set.Add(reader.GetGuid(0));
        }
        return set;
    }

    /// <summary>Links `processing.observations.legacy_target_id` to the rows just written (ADR-0006 open item closed by P09).</summary>
    public static async Task LinkObservationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE processing.observations o SET legacy_target_id = t.target_id FROM targets t WHERE t.observation_id = o.observation_id AND t.raw_message_id = @raw AND o.legacy_target_id IS NULL", conn, tx);
        cmd.Parameters.AddWithValue("raw", rawId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record TrackState(long TrackId, int Revision, string Status, string? ClosedReason, int TargetCategoryId, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt, int TargetCount);

    /// <summary>revision + 1 and the causation chain of the aggregate, in the delivery transaction; returns the new state for the event payload.</summary>
    public static async Task<TrackState> BumpTrackAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long trackId, Guid eventId, Guid correlationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE target_tracks SET revision = revision + 1, last_event_id = @e, last_correlation_id = @c WHERE target_track_id = @id
            RETURNING revision, status, closed_reason, target_category_id, last_seen_at, updated_at, target_count
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", trackId);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("c", correlationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"target_tracks {trackId} vanished inside the delivery transaction");
        }
        return new TrackState(trackId, reader.GetInt32(0), TrackStatusName(reader.GetInt32(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6));
    }

    public sealed record AlertState(long AlertId, int Revision, int PlaceId, string SourceAlertId, int Level, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

    public static async Task<AlertState> BumpAlertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long alertId, Guid eventId, Guid correlationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE air_alerts SET revision = revision + 1, last_event_id = @e, last_correlation_id = @c WHERE air_alert_id = @id
            RETURNING revision, place_id, source_alert_id, level, started_at, ended_at
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", alertId);
        cmd.Parameters.AddWithValue("e", eventId);
        cmd.Parameters.AddWithValue("c", correlationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"air_alerts {alertId} vanished inside the delivery transaction");
        }
        return new AlertState(alertId, reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    /// <summary>Observation ids of this raw that ended up on the track (the payload's `observation_ids`).</summary>
    public static async Task<List<Guid>> TrackObservationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long trackId, long rawId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT t.observation_id FROM track_targets tt JOIN targets t ON t.target_id = tt.target_id WHERE tt.target_track_id = @track AND t.raw_message_id = @raw AND t.observation_id IS NOT NULL", conn, tx);
        cmd.Parameters.AddWithValue("track", trackId);
        cmd.Parameters.AddWithValue("raw", rawId);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <summary>An aggregate-scoped child event: the message identity of the cause plus aggregate id/revision and the partition key.</summary>
    public static Envelope AggregateEvent(Envelope cause, string eventType, string producer, DateTimeOffset occurredAt, string aggregateId, long revision, string partitionKey, JsonObject payload, Guid eventId)
    {
        var e = StageSupport.Child(cause, eventType, SchemaVersion, producer, occurredAt, payload);
        e.EventId = eventId;
        e.AggregateId = aggregateId;
        e.AggregateRevision = revision;
        e.PartitionKey = partitionKey;
        return e;
    }

    public static string TrackStatusName(int status) => status switch
    {
        (int)Domain.Enums.TrackStatus.Active => "active",
        (int)Domain.Enums.TrackStatus.Closed => "closed",
        (int)Domain.Enums.TrackStatus.Cancelled => "cancelled",
        _ => "active",
    };

    public static JsonArray Ids(IEnumerable<Guid> ids) => new(ids.Select(i => (JsonNode)i.ToString()).ToArray());

    /// <summary>All facts of an `observations.recorded` payload; the handlers split them by `category`.</summary>
    public static IEnumerable<JsonObject> Facts(Envelope envelope) =>
        (envelope.Payload?["observations"] as JsonArray ?? []).Select(n => n!.AsObject());
}
