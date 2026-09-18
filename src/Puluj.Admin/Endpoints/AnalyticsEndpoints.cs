using Puluj.Analytics.Persistence;
using Puluj.Analytics.Reporting;

namespace Puluj.Admin;

/// <summary>
/// Operational endpoints for the independent analytics index.
/// </summary>
public static class AnalyticsEndpoints
{
    private const string NotInitialized = "Сервіс аналітики ще не створив свою схему (він ще не запускався).";

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/analytics").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        group.MapGet("/status", (AnalyticsReportService reports, CancellationToken ct) => reports.StatusAsync(ct));
        group.MapPost("/reset", async (AnalyticsReportService reports, CancellationToken ct) =>
        {
            await reports.ResetAsync(ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
