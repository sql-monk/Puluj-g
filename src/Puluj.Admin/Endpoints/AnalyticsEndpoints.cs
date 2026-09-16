using Microsoft.Extensions.Options;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Reporting;

namespace Puluj.Admin;

/// <summary>
/// The source analytics page: results of the analytics service (who copies whom, forwards, activity, track firsts)
/// and the state of the service itself. Read straight from the `analytics` schema the service maintains; `reset`
/// clears it so the service rebuilds everything on its next run.
/// </summary>
public static class AnalyticsEndpoints
{
    private const string NotInitialized = "Сервіс аналітики ще не створив свою схему (він ще не запускався).";

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/analytics").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        group.MapGet("/status", (AnalyticsReportService reports, CancellationToken ct) => reports.StatusAsync(ct));
        group.MapGet("/report", async (int? days, AnalyticsReportService reports, CancellationToken ct) =>
            await reports.ReportAsync(days ?? 14, ct) is { } report ? Results.Ok(report) : Results.NotFound(new { error = NotInitialized }));
        // kind: near | verbatim | forward; primaryOnly: only the earliest original of every copy.
        group.MapGet("/recent", async (int? limit, int? sourceId, string? kind, bool? primaryOnly, AnalyticsReportService reports, CancellationToken ct) =>
        {
            CopyKind? copyKind = null;
            if (!string.IsNullOrEmpty(kind))
            {
                if (!Enum.TryParse<CopyKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.BadRequest(new { error = "kind: near, verbatim або forward" });
                }
                copyKind = parsed;
            }
            return Results.Ok(await reports.RecentAsync(limit ?? 30, sourceId, copyKind, primaryOnly ?? false, ct));
        });
        group.MapGet("/pairs/{copierId:int}/{originalId:int}", async (int copierId, int originalId, int? days, AnalyticsReportService reports, CancellationToken ct) =>
            await reports.PairAsync(copierId, originalId, days ?? 14, ct) is { } pair ? Results.Ok(pair) : Results.NotFound(new { error = NotInitialized }));
        group.MapPost("/reset", async (AnalyticsReportService reports, CancellationToken ct) =>
        {
            await reports.ResetAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // P15 (ADR-0013): the lifecycle projection — report, status, and the audited backfill/reconcile commands.
        group.MapGet("/lifecycle", async (int? hours, Puluj.Analytics.Lifecycle.LifecycleReportService lifecycle, CancellationToken ct) =>
            await lifecycle.ReportAsync(hours ?? 24, ct) is { } report ? Results.Ok(report) : Results.BadRequest(new { error = "hours має бути 24, 168 або 720" }));
        group.MapGet("/lifecycle/status", (Puluj.Analytics.Lifecycle.LifecycleReportService lifecycle, CancellationToken ct) => lifecycle.StatusAsync(ct));
        group.MapPost("/lifecycle/backfill", async (Puluj.Contracts.QuarantineActionRequest req, bool? reset, Puluj.Analytics.Lifecycle.LifecycleBackfill backfill, IOptions<Puluj.Analytics.AnalyticsOptions> options, Puluj.Infrastructure.Messaging.SubscriptionAdmin admin, CancellationToken ct) =>
        {
            if (Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing(req.Actor, req.Reason) is { } error)
            {
                return Results.BadRequest(new { error });
            }
            if (reset == true)
            {
                await backfill.ResetCursorAsync(ct);
            }
            var progress = await backfill.RunAsync(options.Value.LifecycleBackfillBatch, options.Value.LifecycleBackfillBatchesPerPass, ct);
            await admin.AuditAsync("analytics:backfill", null, null, req.Actor.Trim(), req.Reason.Trim(), new { reset = reset == true, progress.Cursor, progress.MaxRawMessageId, progress.Processed, progress.LegacyRows }, ct);
            return Results.Ok(progress);
        });
        group.MapPost("/lifecycle/reconcile", async (Puluj.Contracts.QuarantineActionRequest req, int? hours, Puluj.Analytics.Lifecycle.LifecycleReconciliation reconciliation, IOptions<Puluj.Analytics.AnalyticsOptions> options, Puluj.Infrastructure.Messaging.SubscriptionAdmin admin, CancellationToken ct) =>
        {
            if (Puluj.Admin.Endpoints.MessagingOpsEndpoints.Missing(req.Actor, req.Reason) is { } error)
            {
                return Results.BadRequest(new { error });
            }
            var report = await reconciliation.RunAsync(TimeSpan.FromHours(Math.Clamp(hours ?? 48, 1, 24 * 30)), options.Value.LifecycleReconcileGrace, ct);
            await admin.AuditAsync("analytics:reconcile", null, null, req.Actor.Trim(), req.Reason.Trim(), report, ct);
            return Results.Ok(report);
        });
        return app;
    }
}
