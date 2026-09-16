using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Incidents;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// Plan §8.4 admin commands (P10): merge / split / resolve / retract / confirm / suppress go through the same
/// <see cref="IncidentStateWriter"/> as the worker — same locks, revisions with actor and reason, `incident.changed` in the
/// outbox. Raw evidence is never edited. The outbox must be enabled: a command without its event is refused (409).
/// </summary>
public static class IncidentEndpoints
{
    public sealed record CommandRequest(string? Actor, string? Reason, DateTimeOffset? EffectiveAt);
    public sealed record MergeRequest(long SourceId, long TargetId, string? Actor, string? Reason);
    public sealed record SplitRequest(List<Guid>? ObservationIds, string? Actor, string? Reason);

    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/incidents").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        g.MapGet("", async (string? state, string? kind, int? hours, int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
            var q = db.Incidents.AsNoTracking().Include(i => i.EventKind).Where(i => i.LastReportedAt >= since);
            if (!string.IsNullOrEmpty(state))
            {
                q = q.Where(i => i.State == state);
            }
            if (!string.IsNullOrEmpty(kind))
            {
                q = q.Where(i => i.EventKind!.Code == kind);
            }
            var rows = await q.OrderByDescending(i => i.LastReportedAt).Take(Math.Clamp(limit ?? 200, 1, 1000))
                .Select(i => new { i.IncidentId, Kind = i.EventKind!.Code, i.State, i.Suppressed, i.EventAt, i.FirstReportedAt, i.LastReportedAt, i.LocationPlaceId, i.AccuracyKm, i.SourceCount, i.Revision, i.ClosureReason, i.MergedIntoIncidentId })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // P12 (§8.7): the review queue — incidents the policy could not place with confidence (an `ambiguous` link or near candidates),
        // newest first, with the current state of every candidate so the operator sees what merge is still possible.
        g.MapGet("/review", async (int? hours, int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 720));
            var take = Math.Clamp(limit ?? 100, 1, 500);
            // Incidents first (one row per incident, newest first, one page), then their flagged links — a hot incident with many links cannot crowd out older ones.
            var pageIds = await db.IncidentObservations.AsNoTracking()
                .Where(ReviewPredicate(since))
                .GroupBy(o => o.IncidentId)
                .Select(g => new { IncidentId = g.Key, Last = g.Max(o => o.Incident!.LastReportedAt) })
                .OrderByDescending(g => g.Last)
                .Take(take + 1)
                .ToListAsync(ct);
            var truncated = pageIds.Count > take;
            var ids = pageIds.Take(take).Select(p => p.IncidentId).ToList();
            var flagged = await db.IncidentObservations.AsNoTracking()
                .Where(ReviewPredicate(since))
                .Where(o => ids.Contains(o.IncidentId))
                .Select(o => new { o.IncidentId, o.ObservationId, o.Relation, o.DecisionReason })
                .ToListAsync(ct);
            var candidateIds = flagged.SelectMany(f => CandidateIds(f.DecisionReason)).Distinct().ToList();
            var incidents = await db.Incidents.AsNoTracking().Include(i => i.EventKind).Where(i => ids.Contains(i.IncidentId) || candidateIds.Contains(i.IncidentId)).ToDictionaryAsync(i => i.IncidentId, ct);
            var rows = ids.Select(id =>
            {
                var i = incidents[id];
                var reasons = flagged.Where(f => f.IncidentId == id).ToList();
                var candidates = reasons.SelectMany(f => CandidateIds(f.DecisionReason)).Distinct().Where(c => c != id)
                    .Select(c => incidents.TryGetValue(c, out var ci) ? new { IncidentId = c, Kind = ci.EventKind!.Code, ci.State, ci.Suppressed, ci.MergedIntoIncidentId, ci.Revision, SameKind = ci.EventKindId == i.EventKindId, Mergeable = IncidentStateWriter.PlanMerge(i, ci).Allowed } : null)
                    .Where(c => c is not null).ToList();
                return new
                {
                    i.IncidentId, Kind = i.EventKind!.Code, i.State, i.Suppressed, i.EventAt, i.LastReportedAt, i.LocationPlaceId, i.AccuracyKm, i.SourceCount, i.Revision,
                    flags = reasons.Select(r => new { r.ObservationId, r.Relation, Ambiguous = CandidateIds(r.DecisionReason, "ambiguous"), Near = CandidateIds(r.DecisionReason, "near_candidates") }),
                    candidates,
                };
            }).ToList();
            return Results.Ok(new { since, count = rows.Count, truncated, items = rows });
        });

        // What a merge would do, without doing it: the same rules as the command (IncidentStateWriter.PlanMerge); the target's revision lets the UI refuse a stale confirm.
        g.MapGet("/{id:long}/merge-preview", async (long id, long targetId, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var pair = await db.Incidents.AsNoTracking().Include(i => i.Observations).Where(i => i.IncidentId == id || i.IncidentId == targetId).ToListAsync(ct);
            var source = pair.FirstOrDefault(i => i.IncidentId == id);
            var target = pair.FirstOrDefault(i => i.IncidentId == targetId);
            if (source is null || target is null)
            {
                return Results.NotFound(new { error = "incident not found" });
            }
            var plan = IncidentStateWriter.PlanMerge(source, target);
            return Results.Ok(new
            {
                sourceId = id, targetId, plan.Allowed, plan.Refusal, plan.MovedObservationIds, plan.SourceCountAfter, plan.FirstReportedAtAfter, plan.LastReportedAtAfter, plan.StateAfter,
                plan.LocationFrom, plan.AccuracyKmAfter, ConfidenceAfter = plan.ConfidenceAfter.ToString().ToLowerInvariant(), plan.TargetRevision, sourceRevision = source.Revision,
            });
        });

        g.MapGet("/{id:long}", async (long id, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var incident = await db.Incidents.AsNoTracking().Include(i => i.EventKind).Include(i => i.Observations).SingleOrDefaultAsync(i => i.IncidentId == id, ct);
            if (incident is null)
            {
                return Results.NotFound();
            }
            var revisions = await db.IncidentRevisions.AsNoTracking().Where(r => r.IncidentId == id).OrderBy(r => r.Revision).ToListAsync(ct);
            // The evidence behind every link (P12 review N6): the segment and the raw message — admin-only; the public API returns the DTO shape.
            var targetIds = incident.Observations.Where(o => o.LegacyTargetId != null).Select(o => o.LegacyTargetId!.Value).ToList();
            var targets = await db.Targets.AsNoTracking().Include(t => t.RawMessage).Where(t => targetIds.Contains(t.TargetId)).ToDictionaryAsync(t => t.TargetId, ct);
            var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.SourceId, s => s.Code, ct);
            return Results.Ok(new
            {
                incident.IncidentId, Kind = incident.EventKind!.Code, incident.State, incident.Suppressed, incident.GenerationId, incident.EventAt, incident.FirstReportedAt, incident.LastReportedAt,
                incident.LocationKind, incident.LocationPlaceId, incident.AccuracyKm, incident.Confidence, incident.SourceCount, incident.IndependentSourceCount,
                incident.CanonicalObservationId, incident.Revision, incident.ClosureReason, incident.MergedIntoIncidentId,
                observations = incident.Observations.OrderBy(o => o.EffectiveAt).Select(o =>
                {
                    var t = o.LegacyTargetId is long tid ? targets.GetValueOrDefault(tid) : null;
                    return new
                    {
                        o.ObservationId, o.LegacyTargetId, o.SourceId, SourceCode = sources.GetValueOrDefault(o.SourceId), o.Relation, o.Score, o.EffectiveAt, o.LinkedAt, o.PolicyVersion, DecisionReason = o.DecisionReason,
                        SegmentText = t?.SegmentText, RawText = t?.RawMessage?.RawText, RawUrl = t?.RawMessage?.Url, RawMessageId = t?.RawMessageId,
                    };
                }),
                revisions = revisions.Select(r => new { r.Revision, r.Change, r.EffectiveAt, r.RecordedAt, r.Actor, r.Reason, r.TriggeringEventId, Snapshot = r.Snapshot }),
            });
        });

        g.MapPost("/{id:long}/resolve", (long id, CommandRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Ok(await writer.ResolveAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/retract", (long id, CommandRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Ok(await writer.RetractAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/confirm", (long id, CommandRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Ok(await writer.ConfirmAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/suppress", (long id, CommandRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Ok(await writer.SuppressAsync(id, true, Required(req.Actor), Required(req.Reason), ct))));
        g.MapPost("/{id:long}/unsuppress", (long id, CommandRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Ok(await writer.SuppressAsync(id, false, Required(req.Actor), Required(req.Reason), ct))));
        g.MapPost("/merge", (MergeRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Results.Ok((await writer.MergeAsync(req.SourceId, req.TargetId, Required(req.Actor), Required(req.Reason), ct)).Select(Summary))));
        g.MapPost("/{id:long}/split", (long id, SplitRequest req, IncidentStateWriter writer, AdminIndexes indexes, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, indexes, ct, async () => Results.Ok((await writer.SplitAsync(id, req.ObservationIds ?? [], Required(req.Actor), Required(req.Reason), ct)).Select(Summary))));
        return app;
    }

    /// <summary>The review queue's link predicate (also exercised by the integration test): an `ambiguous` link, or a reason naming near/ambiguous candidates, on a visible incident inside the window.</summary>
    public static System.Linq.Expressions.Expression<Func<IncidentObservation, bool>> ReviewPredicate(DateTimeOffset since) =>
        o => o.Incident!.LastReportedAt >= since && !o.Incident.Suppressed && o.Incident.State != Incident.Retracted
            && (o.Relation == IncidentObservation.Ambiguous || EF.Functions.JsonExists(o.DecisionReason!, "near_candidates") || EF.Functions.JsonExists(o.DecisionReason!, "ambiguous"));

    /// <summary>Incident ids named in a link's decision reason under `ambiguous`, `near_candidates` or `candidates[].incident_id`.</summary>
    private static IReadOnlyList<long> CandidateIds(System.Text.Json.JsonDocument? reason, string? key = null)
    {
        if (reason is null || reason.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return [];
        }
        var ids = new List<long>();
        foreach (var name in key is null ? ["ambiguous", "near_candidates", "candidates"] : new[] { key })
        {
            if (!reason.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    ids.Add(e.GetInt64());
                }
                else if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("incident_id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    ids.Add(id.GetInt64());
                }
            }
        }
        return ids.Distinct().ToList();
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("actor and reason are required") : value;

    private static IResult Ok(IncidentChange change) => Results.Ok(Summary(change));

    private static object Summary(IncidentChange c) => new { c.Incident.IncidentId, c.Change, c.Incident.State, c.Incident.Suppressed, c.Incident.Revision, EventId = c.Event.EventId, ObservationIds = c.ObservationIds };

    /// <summary>
    /// 404 unknown incident, 409 wrong state / kinds / outbox off, 400 missing actor or reason. The admin process has no background
    /// index refresh: the catalog is loaded before a command, so the event carries the kind code, never a numeric id (review B2).
    /// </summary>
    public static async Task<IResult> Guard(IOptions<MessagingOptions> messaging, AdminIndexes? indexes, CancellationToken ct, Func<Task<IResult>> action)
    {
        if (!messaging.Value.Outbox.Enabled)
        {
            return Results.Conflict(new { error = "Messaging:Outbox:Enabled is false: an incident command without its incident.changed event is refused (ADR-0010)" });
        }
        try
        {
            if (indexes is not null)
            {
                await indexes.EnsureFreshAsync(ct);
            }
            return await action();
        }
        catch (IncidentNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (IncidentConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
