using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Contracts;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Reporting;

/// <summary>Operational read side for the independent analytics index and its worker.</summary>
public sealed class AnalyticsReportService(IDbContextFactory<AnalyticsDbContext> factory, IOptions<AnalyticsOptions> options)
{
    public async Task<AnalyticsStatusDto> StatusAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var latest = await RawMessageReader.MaxIdAsync(db, ct);
        var heartbeat = await HeartbeatAsync(db, ct);
        var instance = await InstanceAsync(db, ct);
        if (!await InitializedAsync(db, ct))
            return new AnalyticsStatusDto(false, 0, latest, latest, heartbeat, null, [], 0, 0, [], instance);

        var watermark = await AnalysisRunner.WatermarkAsync(db, ct);
        var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct);
        var counts = (await db.Database.SqlQueryRaw<CountRow>("""
            SELECT (SELECT count(*) FROM analytics.messages) AS messages,
                   (SELECT coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'analytics' AND c.relkind = 'r') AS schema_bytes
            """).ToListAsync(ct)).First();
        var migrations = await db.Database.SqlQueryRaw<string>("SELECT migration_id AS \"Value\" FROM analytics.\"__EFMigrationsHistory\" ORDER BY 1").ToListAsync(ct);
        var dtos = runs.Select(ToDto).ToList();
        return new AnalyticsStatusDto(true, watermark, latest, Math.Max(0, latest - watermark), heartbeat, dtos.FirstOrDefault(), dtos,
            counts.Messages, counts.SchemaBytes, migrations, instance);
    }

    /// <summary>Rewinds only the independent index and track-first projection; raw messages and lifecycle analytics are retained.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.messages", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.track_firsts", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.state WHERE key NOT LIKE 'lifecycle_%'", ct);
        await tx.CommitAsync(ct);
    }

    public static async Task<bool> InitializedAsync(AnalyticsDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('analytics.runs') IS NOT NULL AS \"Value\"").ToListAsync(ct)).First();

    private async Task<DateTimeOffset?> HeartbeatAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var key = $"Runtime:Worker:{options.Value.Name}:Heartbeat";
        var value = (await db.Database.SqlQuery<string>($"SELECT value AS \"Value\" FROM app_settings WHERE key = {key}").ToListAsync(ct)).FirstOrDefault();
        return DateTimeOffset.TryParse(value, out var at) ? at : null;
    }

    private async Task<AnalyticsInstanceDto?> InstanceAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var key = $"Runtime:Worker:{options.Value.Name}:Status";
        var value = (await db.Database.SqlQuery<string>($"SELECT value AS \"Value\" FROM app_settings WHERE key = {key}").ToListAsync(ct)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<StatusDocument>(value, StatusJson);
            return doc is null ? null : new AnalyticsInstanceDto(doc.Host, doc.Version, doc.BuiltAt, doc.StartedAt, doc.At, doc.Pid, doc.WorkingSetBytes, doc.CpuPercent, doc.Threads);
        }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web);
    private static RunDto ToDto(AnalysisRun r) => new(r.RunId, r.Instance, r.StartedAt, r.FinishedAt, r.UpdatedAt, r.Status.ToString().ToLowerInvariant(),
        r.WatermarkFrom, r.WatermarkTo, r.MessagesScanned, r.MessagesFingerprinted, r.Error);

    private sealed record StatusDocument(string? Host, string? Version, DateTimeOffset? BuiltAt, DateTimeOffset? StartedAt, DateTimeOffset? At, int? Pid, long? WorkingSetBytes, double? CpuPercent, int? Threads);
    private sealed record CountRow(long Messages, long SchemaBytes);
}
