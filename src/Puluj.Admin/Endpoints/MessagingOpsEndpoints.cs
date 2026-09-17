using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Puluj.Admin.Docker;
using Puluj.Contracts;
using Puluj.Domain.Entities.Messaging;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Ops;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// P13 (ADR-0012, plan §9/§8.7): the message-platform operations view and its controls. Reads are the snapshot
/// (<see cref="OpsSnapshotService"/>), the quarantine list, the control audit and the message explorer; writes are the
/// operator commands — pause/resume/drain one lane of one subscription, retry/waive one quarantined delivery, scale a
/// compose service — each leaves an audit row. The single-operator UI uses explicit local-admin defaults, while API
/// clients may still attach an actor and reason. RBAC is the admin token; roles are P16.
/// </summary>
public static class MessagingOpsEndpoints
{
    public static IEndpointRouteBuilder MapMessagingOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        g.MapGet("/ops/messaging", async (bool? fresh, OpsSnapshotService snapshots, CancellationToken ct) => Results.Ok(await snapshots.SnapshotAsync(ct, fresh ?? false)));

        g.MapGet("/ops/messaging/quarantine", async (string? subscription, bool? open, int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.Quarantine.AsNoTracking();
            if (!string.IsNullOrEmpty(subscription))
            {
                query = query.Where(q => q.SubscriptionId == subscription);
            }
            if (open ?? true)
            {
                query = query.Where(q => q.ResolvedAt == null);
            }
            var rows = await query.OrderByDescending(q => q.QuarantinedAt).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(q =>
            {
                var root = q.Envelope.RootElement;
                return new QuarantineRowDto(q.QuarantineId, q.SubscriptionId, q.EventId, q.Lane, q.Reason, q.Error, q.QuarantinedAt, q.ResolvedAt, q.ResolvedBy, q.Resolution,
                    root.ValueKind == JsonValueKind.Object && root.TryGetProperty("event_type", out var t) ? t.GetString() : null,
                    root.ValueKind == JsonValueKind.Object && root.TryGetProperty("raw_message_id", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null);
            }));
        });

        g.MapGet("/ops/messaging/audit", async (int? limit, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.ControlAudits.AsNoTracking().OrderByDescending(a => a.At).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(a => new ControlAuditDto(a.AuditId, a.Action, a.SubscriptionId, a.Lane, a.Actor, a.Reason, a.At, a.Details?.RootElement.Clone())));
        });

        // Lane control: the consumers react on their next control poll (≤ Messaging:Consumer:ControlPoll + in-flight).
        g.MapPost("/ops/messaging/lanes/{subscription}/{lane}", async (string subscription, string lane, LaneControlRequest req, SubscriptionAdmin admin, OpsSnapshotService snapshots, CancellationToken ct) =>
        {
            if (req.State is not (SubscriptionLane.Active or SubscriptionLane.Paused or SubscriptionLane.Draining))
            {
                return Results.BadRequest(new { error = "state має бути active, paused або draining" });
            }
            var audit = Audit(req.Actor, req.Reason);
            try
            {
                await admin.SetLaneStateAsync(subscription, lane, req.State, audit.Actor, audit.Reason, ct);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            var snapshot = await snapshots.SnapshotAsync(ct, fresh: true);
            return Results.Ok(new { ok = true, scope = $"{subscription}/{lane}", state = req.State, lane = snapshot.Subscriptions.FirstOrDefault(s => s.Subscription == subscription && s.Lane == lane) });
        });

        g.MapPost("/ops/messaging/quarantine/{id:long}/retry", async (long id, QuarantineActionRequest req, SubscriptionAdmin admin, CancellationToken ct) =>
        {
            var audit = Audit(req.Actor, req.Reason);
            try
            {
                var outboxId = await admin.RetryAsync(id, audit.Actor, audit.Reason, ct);
                return Results.Ok(new { ok = true, outboxId });
            }
            catch (InvalidOperationException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });

        g.MapPost("/ops/messaging/quarantine/{id:long}/waive", async (long id, QuarantineActionRequest req, SubscriptionAdmin admin, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            var audit = Audit(req.Actor, req.Reason);
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Quarantine.AsNoTracking().FirstOrDefaultAsync(q => q.QuarantineId == id && q.ResolvedAt == null, ct);
            if (row is null)
            {
                return Results.Conflict(new { error = $"карантин {id} не знайдено або вже вирішено" });
            }
            var waived = await admin.WaiveAsync(row.SubscriptionId, audit.Reason, audit.Actor, [row.EventId], ct);
            return Results.Ok(new { ok = true, waived });
        });

        // Scale a compose service (processor | messaging); refused outside Docker (no simulation), always audited.
        g.MapPost("/ops/messaging/scale", async (MessagingScaleRequest req, HttpContext http, DockerService docker, SubscriptionAdmin admin, CancellationToken ct) =>
        {
            var audit = Audit(req.Actor, req.Reason);
            // The allow-list is Docker:ScalableServices (DockerService answers 400 for anything else); a refused request is not audited.
            var outcome = await docker.ScaleAsync(req.Service ?? "", req.Replicas, http.Connection.RemoteIpAddress?.ToString(), ct);
            if (outcome.StatusCode != 400)
            {
                await admin.AuditAsync("scale", null, null, audit.Actor, audit.Reason, new { req.Service, req.Replicas, ok = outcome.Result.Ok, outcome.Result.Message }, ct);
            }
            return Results.Json(outcome.Result, statusCode: outcome.StatusCode == 503 ? 409 : outcome.StatusCode);
        });

        // Message explorer.
        g.MapGet("/messages", async (string? q, int? sourceId, int[]? sourceIds, int? hours, int? page, int? limit, string? view, string? sort, string? direction, MessageExplorer explorer, CancellationToken ct) =>
        {
            if (!MessageExplorer.IsValidView(view))
            {
                return Results.BadRequest(new { error = $"view має бути одним із: {string.Join(", ", MessageExplorer.Views)}" });
            }
            if (!MessageExplorer.IsValidSort(sort) || !MessageExplorer.IsValidDirection(direction))
            {
                return Results.BadRequest(new { error = $"sort має бути одним із: {string.Join(", ", MessageExplorer.Sorts)}; direction — asc або desc" });
            }
            // sourceId stays accepted for existing deep links; sourceIds permits one, several, or every source.
            var selected = (sourceIds ?? []).Where(id => id > 0).Distinct().ToList();
            if (sourceId is > 0 && !selected.Contains(sourceId.Value))
            {
                selected.Add(sourceId.Value);
            }
            return Results.Ok(await explorer.SearchPageAsync(q, selected, hours, page, limit, view, sort, direction, ct));
        });
        g.MapGet("/messages/{rawId:long}/lifecycle", async (long rawId, MessageExplorer explorer, CancellationToken ct) =>
            await explorer.LifecycleAsync(rawId, ct) is { } card ? Results.Ok(card) : Results.NotFound());

        return app;
    }

    /// <summary>Validation shared by every command: actor and reason present and within the audit column widths; null = ok, otherwise the 400 message.</summary>
    public static string? Missing(string? actor, string? reason) =>
        string.IsNullOrWhiteSpace(actor) ? "actor обовʼязковий"
        : string.IsNullOrWhiteSpace(reason) ? "reason обовʼязковий"
        : actor.Trim().Length > SubscriptionAdmin.MaxActor ? $"actor ≤ {SubscriptionAdmin.MaxActor} символів"
        : reason.Trim().Length > SubscriptionAdmin.MaxReason ? $"reason ≤ {SubscriptionAdmin.MaxReason} символів"
        : null;

    /// <summary>Audit provenance for the single-user panel. Explicit API values stay available for automation.</summary>
    public static (string Actor, string Reason) Audit(string? actor, string? reason)
    {
        const string defaultActor = "local-admin";
        const string defaultReason = "manual action from admin UI";
        var normalizedActor = string.IsNullOrWhiteSpace(actor) ? defaultActor : actor.Trim();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? defaultReason : reason.Trim();
        return (normalizedActor.Length <= SubscriptionAdmin.MaxActor ? normalizedActor : defaultActor,
            normalizedReason.Length <= SubscriptionAdmin.MaxReason ? normalizedReason : defaultReason);
    }
}
