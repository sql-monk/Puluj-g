using Puluj.Contracts;
using Puluj.Infrastructure.Processing;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// P14 (ADR-0005, plan §11): replay runs and generations — list, create a replay run over a scope, drive its state machine
/// (start/pause/resume/cancel/catchup/verify/promote/rollback). Every command needs actor + reason (400), an unknown run is 404,
/// a transition the state machine refuses (or a verify gate that fails) is 409 with the reason; every command is audited.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/ops/runs").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        g.MapGet("", async (int? limit, RunService runs, CancellationToken ct) => Results.Ok(await runs.ListAsync(limit ?? 50, ct)));

        g.MapPost("/replay", async (ReplayCreateRequest req, RunService runs, CancellationToken ct) =>
        {
            if (MessagingOpsEndpoints.Missing(req.Actor, req.Reason) is { } error)
            {
                return Results.BadRequest(new { error });
            }
            try
            {
                var runId = await runs.CreateReplayAsync(new RunService.ReplayScope(req.SourceIds is { Length: > 0 } ? req.SourceIds : null, req.From, req.To, null), req.Actor.Trim(), req.Reason.Trim(), ct);
                return Results.Ok(new { ok = true, runId });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            catch (RunConflictException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });

        g.MapPost("/{id:guid}/{action:regex(^(start|pause|resume|cancel|catchup|verify|promote|rollback)$)}", async (Guid id, string action, RunActionRequest req, bool? force, RunService runs, CancellationToken ct) =>
        {
            if (MessagingOpsEndpoints.Missing(req.Actor, req.Reason) is { } error)
            {
                return Results.BadRequest(new { error });
            }
            var actor = req.Actor.Trim();
            var reason = req.Reason.Trim();
            try
            {
                object result = action switch
                {
                    "start" => await Do(runs.StartAsync(id, actor, reason, ct)),
                    "pause" => await Do(runs.PauseAsync(id, actor, reason, ct)),
                    "resume" => await Do(runs.ResumeAsync(id, actor, reason, ct)),
                    "cancel" => await Do(runs.CancelAsync(id, actor, reason, ct)),
                    "catchup" => new { ok = true, watermark = await runs.CatchUpAsync(id, req.Watermark, actor, reason, ct) },
                    "verify" => new { ok = true, report = await runs.VerifyAsync(id, actor, reason, ct) },
                    "promote" => new { ok = true, generation = await runs.PromoteAsync(id, actor, reason, ct, force ?? false) },
                    "rollback" => new { ok = true, restored = await runs.RollbackAsync(id, actor, reason, ct) },
                    _ => throw new InvalidOperationException(action),
                };
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (RunConflictException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        return app;
    }

    private static async Task<object> Do(Task task)
    {
        await task;
        return new { ok = true };
    }
}
