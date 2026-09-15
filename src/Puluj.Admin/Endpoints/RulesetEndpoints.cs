using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Rules;
using Puluj.Processing.Indexes;
using Puluj.Processing.Rules;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// Plan §8.3 authoring flow (P08): rule-set versions, draft → rules → validate → preview/corpus → shadow → publish, and
/// rollback. Every mutation needs an actor and a reason (audited by <see cref="RulesetService"/>); authorization is the
/// admin bearer token (per-role RBAC — P12/P13). Preview and corpus run the real parser with the draft as the pinned set.
/// </summary>
public static class RulesetEndpoints
{
    public sealed record MutationRequest(string? Actor, string? Reason);
    public sealed record CreateDraftRequest(int? ParentVersion, string? Actor, string? Reason);
    public sealed record ReplaceRulesRequest(List<RuleDefinition>? Rules, string? Actor, string? Reason);
    public sealed record RollbackRequest(int Version, string? Actor, string? Reason);
    public sealed record PreviewRequest(List<string>? Texts, int? BaselineVersion, string? SourceCode);
    public sealed record CorpusRequest(string? SourceCode);

    public const int MaxPreviewTexts = 200;

    public static IEndpointRouteBuilder MapRulesetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/rulesets").AddEndpointFilter(AdminEndpoints.AuthorizeAsync);

        g.MapGet("", async (RulesetService rulesets, CancellationToken ct) => Results.Ok(await rulesets.ListAsync(ct)));
        g.MapGet("/{version:int}", (int version, RulesetService rulesets, CancellationToken ct) => Guard(async () => Results.Ok(await rulesets.GetAsync(version, ct))));

        g.MapPost("", (CreateDraftRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            var version = await rulesets.CreateDraftAsync(req.ParentVersion, req.Actor!, req.Reason!, ct);
            return Results.Created($"/api/admin/rulesets/{version}", new { version });
        }));

        g.MapPut("/{version:int}/rules", (int version, ReplaceRulesRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            if (req.Rules is null)
            {
                return Results.BadRequest(new { error = "rules are required" });
            }
            await rulesets.ReplaceRulesAsync(version, req.Rules, req.Actor!, req.Reason!, ct);
            return Results.Ok(await rulesets.GetAsync(version, ct));
        }));

        g.MapPost("/{version:int}/validate", (int version, MutationRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad; // validation is audited too
            }
            return Results.Ok(await rulesets.ValidateAsync(version, req.Actor!, ct));
        }));

        g.MapPost("/{version:int}/preview", (int version, PreviewRequest req, RulesetService rulesets, AdminIndexes indexes, RulesetPreview preview, CancellationToken ct) => Guard(async () =>
        {
            if (req.Texts is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "texts[] is required (1–200 messages)" });
            }
            if (req.Texts.Count > MaxPreviewTexts)
            {
                return Results.BadRequest(new { error = $"at most {MaxPreviewTexts} texts per preview" });
            }
            await indexes.EnsureFreshAsync(ct);
            var candidate = await LoadAsync(rulesets, version, ct);
            var baseline = req.BaselineVersion is int b ? await LoadAsync(rulesets, b, ct) : indexes.Provider.Rules;
            return Results.Ok(new { candidate = candidate.Id, baseline = baseline.Id, texts = preview.Compare(baseline, candidate, req.Texts, req.SourceCode) });
        }));

        g.MapPost("/{version:int}/corpus", (int version, CorpusRequest? req, RulesetService rulesets, AdminIndexes indexes, RulesetEvaluator evaluator, KindCorpus corpus, CancellationToken ct) => Guard(async () =>
        {
            await indexes.EnsureFreshAsync(ct);
            var candidate = await LoadAsync(rulesets, version, ct);
            var cases = await corpus.LoadAsync(ct);
            if (cases.Count == 0)
            {
                return Results.BadRequest(new { error = "the kind corpus has no cases" }); // never a "perfect" score on nothing
            }
            return Results.Ok(evaluator.Evaluate(candidate, cases, req?.SourceCode));
        }));

        g.MapPost("/{version:int}/shadow", (int version, MutationRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            await rulesets.StartShadowAsync(version, req.Actor!, req.Reason!, ct);
            return Results.Ok(await rulesets.GetAsync(version, ct));
        }));

        g.MapPost("/{version:int}/shadow/stop", (int version, MutationRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            await rulesets.StopShadowAsync(version, req.Actor!, req.Reason!, ct);
            return Results.Ok(await rulesets.GetAsync(version, ct));
        }));

        g.MapGet("/{version:int}/shadow/report", (int version, DateTimeOffset? since, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
            Results.Ok(await rulesets.ShadowReportAsync(version, since, ct))));

        g.MapPost("/{version:int}/publish", (int version, MutationRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            var report = await rulesets.PublishAsync(version, req.Actor!, req.Reason!, ct);
            return Results.Ok(new { published = version, validation = report });
        }));

        g.MapPost("/rollback", (RollbackRequest req, RulesetService rulesets, CancellationToken ct) => Guard(async () =>
        {
            if (Missing(req.Actor, req.Reason) is { } bad)
            {
                return bad;
            }
            await rulesets.RollbackAsync(req.Version, req.Actor!, req.Reason!, ct);
            return Results.Ok(new { active = req.Version });
        }));

        return app;
    }

    private static async Task<RulesetIndex> LoadAsync(RulesetService rulesets, int version, CancellationToken ct) =>
        RulesetIndex.From(await rulesets.LoadAsync(version, ct) ?? throw new RulesetNotFoundException(version));

    /// <summary>actor/reason are the audit trail: a mutation without them is refused, not defaulted.</summary>
    public static IResult? Missing(string? actor, string? reason) =>
        string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason) ? Results.BadRequest(new { error = "actor and reason are required" }) : null;

    /// <summary>Maps the service's outcomes to HTTP: 404 unknown version, 409 wrong state / lost race, 422 validation errors on publish.</summary>
    public static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RulesetNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (RulesetConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (RulesetValidationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message, validation = ex.Report });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (DbUpdateException ex)
        {
            return Results.Conflict(new { error = ex.InnerException?.Message ?? ex.Message });
        }
    }
}

/// <summary>
/// The parser's indexes in the admin process: taxonomy/gazetteer/kinds loaded on first use and refreshed at most every
/// 10 minutes, without the worker's background refresh (review N8); the rule-set pointers (active/shadow) are re-read on
/// every call, so the default preview baseline is the version that is live right now. Preview/corpus pass the draft explicitly.
/// </summary>
public sealed class AdminIndexes(IndexProvider provider, IDbContextFactory<PulujDbContext> factory, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public IndexProvider Provider => provider;

    public async Task EnsureFreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - _loadedAt >= Ttl)
            {
                await provider.RefreshAsync(ct);
                _loadedAt = clock.GetUtcNow();
            }
            else
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await provider.RefreshRulesAsync(db, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>`data/corpus/kinds.json` (golden kind cases) as shipped with the process (`Seed:DataDirectory`); a missing file is an error, not an empty corpus.</summary>
public sealed class KindCorpus(Puluj.Infrastructure.Seeding.SeedFiles files)
{
    public const string FileName = "corpus/kinds.json";

    public async Task<IReadOnlyList<KindCase>> LoadAsync(CancellationToken ct) =>
        (await files.ReadAsync<KindCorpusFile>(FileName, ct))?.Cases ?? throw new FileNotFoundException($"{FileName} not found under {files.Root}");
}
