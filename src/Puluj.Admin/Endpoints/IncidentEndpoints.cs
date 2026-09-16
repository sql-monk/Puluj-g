using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

        g.MapGet("/{id:long}", async (long id, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var incident = await db.Incidents.AsNoTracking().Include(i => i.EventKind).Include(i => i.Observations).SingleOrDefaultAsync(i => i.IncidentId == id, ct);
            if (incident is null)
            {
                return Results.NotFound();
            }
            var revisions = await db.IncidentRevisions.AsNoTracking().Where(r => r.IncidentId == id).OrderBy(r => r.Revision).ToListAsync(ct);
            return Results.Ok(new
            {
                incident.IncidentId, Kind = incident.EventKind!.Code, incident.State, incident.Suppressed, incident.GenerationId, incident.EventAt, incident.FirstReportedAt, incident.LastReportedAt,
                incident.LocationKind, incident.LocationPlaceId, incident.AccuracyKm, incident.Confidence, incident.SourceCount, incident.IndependentSourceCount,
                incident.CanonicalObservationId, incident.Revision, incident.ClosureReason, incident.MergedIntoIncidentId,
                observations = incident.Observations.OrderBy(o => o.EffectiveAt).Select(o => new { o.ObservationId, o.LegacyTargetId, o.SourceId, o.Relation, o.Score, o.EffectiveAt, o.LinkedAt, o.PolicyVersion, DecisionReason = o.DecisionReason }),
                revisions = revisions.Select(r => new { r.Revision, r.Change, r.EffectiveAt, r.RecordedAt, r.Actor, r.Reason, r.TriggeringEventId, Snapshot = r.Snapshot }),
            });
        });

        g.MapPost("/{id:long}/resolve", (long id, CommandRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Ok(await writer.ResolveAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/retract", (long id, CommandRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Ok(await writer.RetractAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/confirm", (long id, CommandRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Ok(await writer.ConfirmAsync(id, Required(req.Actor), Required(req.Reason), req.EffectiveAt, ct))));
        g.MapPost("/{id:long}/suppress", (long id, CommandRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Ok(await writer.SuppressAsync(id, true, Required(req.Actor), Required(req.Reason), ct))));
        g.MapPost("/{id:long}/unsuppress", (long id, CommandRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Ok(await writer.SuppressAsync(id, false, Required(req.Actor), Required(req.Reason), ct))));
        g.MapPost("/merge", (MergeRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Results.Ok((await writer.MergeAsync(req.SourceId, req.TargetId, Required(req.Actor), Required(req.Reason), ct)).Select(Summary))));
        g.MapPost("/{id:long}/split", (long id, SplitRequest req, IncidentStateWriter writer, IOptions<MessagingOptions> messaging, CancellationToken ct) =>
            Guard(messaging, async () => Results.Ok((await writer.SplitAsync(id, req.ObservationIds ?? [], Required(req.Actor), Required(req.Reason), ct)).Select(Summary))));
        return app;
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("actor and reason are required") : value;

    private static IResult Ok(IncidentChange change) => Results.Ok(Summary(change));

    private static object Summary(IncidentChange c) => new { c.Incident.IncidentId, c.Change, c.Incident.State, c.Incident.Suppressed, c.Incident.Revision, EventId = c.Event.EventId, ObservationIds = c.ObservationIds };

    /// <summary>404 unknown incident, 409 wrong state / kinds / outbox off, 400 missing actor or reason.</summary>
    public static async Task<IResult> Guard(IOptions<MessagingOptions> messaging, Func<Task<IResult>> action)
    {
        if (!messaging.Value.Outbox.Enabled)
        {
            return Results.Conflict(new { error = "Messaging:Outbox:Enabled is false: an incident command without its incident.changed event is refused (ADR-0010)" });
        }
        try
        {
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
