using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Rules;

/// <summary>The request cannot be applied in the rule set's current state.</summary>
public sealed class RulesetConflictException(string message) : Exception(message);

public sealed class RulesetNotFoundException(int version) : Exception($"rule set v{version} does not exist");

/// <summary>Publishing refused: the validator reported errors (the report is in <see cref="Report"/>).</summary>
public sealed class RulesetValidationException(ValidationReport report) : Exception("rule set has validation errors")
{
    public ValidationReport Report { get; } = report;
}

/// <summary>A rule as loaded for the resolver: the kind resolved to its code and enabled flag.</summary>
public sealed record LoadedRule(RuleDefinition Definition, bool KindEnabled);

public sealed record RulesetSnapshotData(int Version, string State, bool IsActive, IReadOnlyList<LoadedRule> Rules);

public sealed record RulesetSummary(int Version, string State, bool IsActive, int? ParentVersion, DateTimeOffset CreatedAt, string CreatedBy, string Reason,
    DateTimeOffset? PublishedAt, string? PublishedBy, int RuleCount);

public sealed record RulesetDetails(RulesetSummary Summary, IReadOnlyList<RuleDefinition> Rules, IReadOnlyList<EventKindRulesetAudit> Audit);

/// <summary>
/// Authoring flow: draft → rules → validate → publish → rollback, every step audited with
/// actor and reason. Rows of a non-draft version are never updated; publishing and rollback only move the
/// <c>is_active</c> pointer (one active version, enforced by a partial unique index) and the state column.
/// </summary>
public sealed class RulesetService(IDbContextFactory<PulujDbContext> factory, TimeProvider clock, ILogger<RulesetService> logger)
{
    public async Task<IReadOnlyList<RulesetSummary>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.EventKindRulesets.AsNoTracking().OrderByDescending(r => r.Version)
            .Select(r => new RulesetSummary(r.Version, r.State, r.IsActive, r.ParentVersion, r.CreatedAt, r.CreatedBy, r.Reason, r.PublishedAt, r.PublishedBy, r.Rules.Count))
            .ToListAsync(ct);
    }

    public async Task<RulesetDetails> GetAsync(int version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.EventKindRulesets.AsNoTracking().Include(r => r.Rules).SingleOrDefaultAsync(r => r.Version == version, ct) ?? throw new RulesetNotFoundException(version);
        var kinds = await db.EventKinds.AsNoTracking().ToDictionaryAsync(k => k.EventKindId, k => k.Code, ct);
        var audit = await db.EventKindRulesetAudits.AsNoTracking().Where(a => a.Version == version).OrderBy(a => a.AuditId).ToListAsync(ct);
        var rules = row.Rules.OrderByDescending(r => r.Priority).ThenBy(r => r.RuleCode, StringComparer.Ordinal).Select(r => ToDefinition(r, kinds)).ToList();
        return new RulesetDetails(Summary(row), rules, audit);
    }

    public async Task<RulesetSnapshotData?> LoadAsync(int version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await LoadAsync(db, version, ct);
    }

    public static async Task<RulesetSnapshotData?> LoadAsync(PulujDbContext db, int version, CancellationToken ct)
    {
        var row = await db.EventKindRulesets.AsNoTracking().Include(r => r.Rules).SingleOrDefaultAsync(r => r.Version == version, ct);
        if (row is null)
        {
            return null;
        }
        var kinds = await db.EventKinds.AsNoTracking().ToDictionaryAsync(k => k.EventKindId, ct);
        var codes = kinds.ToDictionary(k => k.Key, k => k.Value.Code);
        var rules = row.Rules.Select(r => new LoadedRule(ToDefinition(r, codes), kinds[r.EventKindId].Enabled)).ToList();
        return new RulesetSnapshotData(row.Version, row.State, row.IsActive, rules);
    }

    /// <summary>A new draft: a copy of the parent's rules (the active version by default; an empty set when there is none yet).</summary>
    public async Task<int> CreateDraftAsync(int? parentVersion, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var parent = parentVersion is int pv
            ? await db.EventKindRulesets.Include(r => r.Rules).SingleOrDefaultAsync(r => r.Version == pv, ct) ?? throw new RulesetNotFoundException(pv)
            : await db.EventKindRulesets.Include(r => r.Rules).SingleOrDefaultAsync(r => r.IsActive, ct);
        var version = (await db.EventKindRulesets.MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
        var now = clock.GetUtcNow();
        var draft = new EventKindRuleset { Version = version, State = EventKindRuleset.Draft, ParentVersion = parent?.Version, CreatedAt = now, CreatedBy = actor, Reason = reason };
        foreach (var r in parent?.Rules ?? [])
        {
            draft.Rules.Add(Copy(r, version, actor, now));
        }
        db.EventKindRulesets.Add(draft);
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.Created, actor, reason, now, new JsonObject { ["parent_version"] = parent?.Version, ["rules"] = draft.Rules.Count }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return version;
    }

    /// <summary>Replaces the rules of a draft. rule_version: kept from the parent's rule with the same code when the behaviour is unchanged, otherwise +1; new rules start at 1.</summary>
    public async Task ReplaceRulesAsync(int version, IReadOnlyList<RuleDefinition> rules, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var row = await db.EventKindRulesets.Include(r => r.Rules).SingleOrDefaultAsync(r => r.Version == version, ct) ?? throw new RulesetNotFoundException(version);
        if (row.State != EventKindRuleset.Draft)
        {
            throw new RulesetConflictException($"rule set v{version} is {row.State}; only a draft can change (create a new draft)");
        }
        var kinds = await db.EventKinds.AsNoTracking().ToDictionaryAsync(k => k.Code, k => k, StringComparer.Ordinal, ct);
        var parentRules = row.ParentVersion is int pv
            ? await db.EventKindRules.AsNoTracking().Where(r => r.RulesetVersion == pv).ToListAsync(ct)
            : [];
        var kindCodes = kinds.ToDictionary(k => k.Value.EventKindId, k => k.Key);
        var parentByCode = parentRules.ToDictionary(r => r.RuleCode, r => (Row: r, Def: ToDefinition(r, kindCodes)), StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var duplicates = rules.GroupBy(r => r.RuleCode, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new RulesetConflictException($"duplicate rule_code: {string.Join(", ", duplicates)}");
        }
        db.EventKindRules.RemoveRange(row.Rules);
        row.Rules.Clear();
        var bumped = 0;
        foreach (var def in rules)
        {
            if (!kinds.TryGetValue(def.EventKindCode, out var kind))
            {
                throw new RulesetConflictException($"rule '{def.RuleCode}': unknown event kind '{def.EventKindCode}'");
            }
            if (def.PositivePatterns.Any(p => p.Type != PatternDefinition.StemsType) || (def.NegativePatterns ?? []).Any(p => p.Type != PatternDefinition.StemsType))
            {
                throw new RulesetConflictException($"rule '{def.RuleCode}': only 'stems' patterns are supported");
            }
            if (def.RuleCode.Length > 128 || def.LanguageOrAny.Length > 8)
            {
                throw new RulesetConflictException($"rule '{def.RuleCode}': rule_code (≤ 128) or language (≤ 8) too long");
            }
            // rule_version is derived, never taken from the payload: the parent's value (kept or +1), or 1 for a rule the parent lacks.
            var ruleVersion = 1;
            if (parentByCode.TryGetValue(def.RuleCode, out var parent))
            {
                var same = parent.Def.BehaviourKey() == (def with { RuleVersion = parent.Def.RuleVersion }).BehaviourKey();
                ruleVersion = same ? parent.Row.RuleVersion : parent.Row.RuleVersion + 1;
                if (!same)
                {
                    bumped++;
                }
            }
            row.Rules.Add(new EventKindRule
            {
                RulesetVersion = version,
                RuleCode = def.RuleCode,
                EventKindId = kind.EventKindId,
                Language = def.LanguageOrAny,
                SourceScope = def.Sources is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(new { sources = def.Sources })),
                PositivePatterns = JsonDocument.Parse(JsonSerializer.Serialize(def.PositivePatterns, Json)),
                NegativePatterns = def.NegativePatterns is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(def.NegativePatterns, Json)),
                Priority = def.Priority,
                ExtractionHints = def.ExtractionHints is { ValueKind: JsonValueKind.Object } h ? JsonDocument.Parse(h.GetRawText()) : null,
                ConfidenceModifier = def.ConfidenceModifier ?? 0,
                RuleVersion = ruleVersion,
                Enabled = def.Enabled ?? true,
                Actor = actor,
                Reason = reason,
                CreatedAt = now,
            });
        }
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.RulesReplaced, actor, reason, now, new JsonObject { ["rules"] = rules.Count, ["rule_version_bumped"] = bumped }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ValidationReport> ValidateAsync(int version, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var report = await ValidateAsync(db, version, ct);
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.Validated, actor, "validate", clock.GetUtcNow(), ReportJson(report)));
        await db.SaveChangesAsync(ct);
        return report;
    }

    private static async Task<ValidationReport> ValidateAsync(PulujDbContext db, int version, CancellationToken ct)
    {
        var snapshot = await LoadAsync(db, version, ct) ?? throw new RulesetNotFoundException(version);
        var kinds = await db.EventKinds.AsNoTracking().ToDictionaryAsync(k => k.Code, k => k.Enabled, StringComparer.Ordinal, ct);
        var sources = (await db.Sources.AsNoTracking().Select(s => s.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return RulesetValidator.Validate(snapshot.Rules.Select(r => r.Definition).ToList(), kinds, sources);
    }

    /// <summary>Validates (always, so a stale earlier validation cannot be reused), then makes the version the active one; the previous active becomes superseded.</summary>
    public async Task<ValidationReport> PublishAsync(int version, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var row = await db.EventKindRulesets.SingleOrDefaultAsync(r => r.Version == version, ct) ?? throw new RulesetNotFoundException(version);
        if (row.State != EventKindRuleset.Draft)
        {
            throw new RulesetConflictException($"rule set v{version} is {row.State}; publish a draft (use rollback for an older published version)");
        }
        var now = clock.GetUtcNow();
        var report = await ValidateAsync(db, version, ct);
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.Validated, actor, reason, now, ReportJson(report)));
        if (!report.Ok)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct); // the failed validation stays on record
            throw new RulesetValidationException(report);
        }
        var previous = await DeactivateAsync(db, ct);
        row.State = EventKindRuleset.Published;
        row.IsActive = true;
        row.PublishedAt = now;
        row.PublishedBy = actor;
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.PublishedAction, actor, reason, now, new JsonObject { ["previous_active"] = previous }));
        await SaveActivationAsync(db, tx, ct);
        logger.LogInformation("Rule set v{Version} published by {Actor} ({Reason}); previous active: {Previous}", version, actor, reason, previous?.ToString() ?? "none");
        return report;
    }

    /// <summary>Activates an older published version for new jobs; stored results are untouched. The current active becomes superseded.</summary>
    public async Task RollbackAsync(int version, string actor, string reason, CancellationToken ct)
    {
        RequireActor(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var row = await db.EventKindRulesets.SingleOrDefaultAsync(r => r.Version == version, ct) ?? throw new RulesetNotFoundException(version);
        if (row.State is not (EventKindRuleset.Published or EventKindRuleset.Superseded))
        {
            throw new RulesetConflictException($"rule set v{version} is {row.State}; rollback needs a version that was published before");
        }
        if (row.IsActive)
        {
            throw new RulesetConflictException($"rule set v{version} is already active");
        }
        var now = clock.GetUtcNow();
        var previous = await DeactivateAsync(db, ct);
        row.State = EventKindRuleset.Published;
        row.IsActive = true;
        db.EventKindRulesetAudits.Add(Audit(version, EventKindRulesetAudit.RolledBack, actor, reason, now, new JsonObject { ["from_version"] = previous }));
        await SaveActivationAsync(db, tx, ct);
        logger.LogWarning("Rule set rolled back to v{Version} by {Actor} ({Reason}); previous active: {Previous}", version, actor, reason, previous?.ToString() ?? "none");
    }

    // ---- helpers ----

    /// <summary>Clears the active pointer in its own statement first: the partial unique index is checked per statement, so activating before deactivating would violate it.</summary>
    private static async Task<int?> DeactivateAsync(PulujDbContext db, CancellationToken ct)
    {
        var previous = await db.EventKindRulesets.Where(r => r.IsActive).ToListAsync(ct);
        foreach (var p in previous)
        {
            p.IsActive = false;
            p.State = EventKindRuleset.Superseded;
        }
        await db.SaveChangesAsync(ct);
        return previous.Count == 0 ? null : previous[0].Version;
    }

    private static async Task SaveActivationAsync(PulujDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new RulesetConflictException($"activation lost a race with another publish/rollback: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>All state transitions serialize on one advisory lock, so two concurrent publishes cannot both read "no active".</summary>
    private static Task LockAsync(PulujDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('event_kind_rulesets'))", ct);

    private static void RequireActor(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("actor and reason are required");
        }
        if (actor.Length > 128 || reason.Length > 1000)
        {
            throw new ArgumentException("actor (≤ 128) or reason (≤ 1000) too long");
        }
    }

    private static EventKindRulesetAudit Audit(int version, string action, string actor, string reason, DateTimeOffset at, JsonObject? details) => new()
    {
        Version = version,
        Action = action,
        Actor = actor,
        Reason = reason,
        At = at,
        Details = details is null ? null : JsonDocument.Parse(details.ToJsonString()),
    };

    private static JsonObject ReportJson(ValidationReport report) => JsonNode.Parse(JsonSerializer.Serialize(report, Json))!.AsObject();

    private static RulesetSummary Summary(EventKindRuleset r) =>
        new(r.Version, r.State, r.IsActive, r.ParentVersion, r.CreatedAt, r.CreatedBy, r.Reason, r.PublishedAt, r.PublishedBy, r.Rules.Count);

    private static EventKindRule Copy(EventKindRule r, int version, string actor, DateTimeOffset now) => new()
    {
        RulesetVersion = version,
        RuleCode = r.RuleCode,
        EventKindId = r.EventKindId,
        Language = r.Language,
        SourceScope = Clone(r.SourceScope),
        PositivePatterns = Clone(r.PositivePatterns)!,
        NegativePatterns = Clone(r.NegativePatterns),
        Priority = r.Priority,
        ExtractionHints = Clone(r.ExtractionHints),
        ConfidenceModifier = r.ConfidenceModifier,
        RuleVersion = r.RuleVersion,
        Enabled = r.Enabled,
        Actor = actor,
        Reason = "copied from v" + r.RulesetVersion,
        CreatedAt = now,
    };

    private static JsonDocument? Clone(JsonDocument? d) => d is null ? null : JsonDocument.Parse(d.RootElement.GetRawText());

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RuleDefinition ToDefinition(EventKindRule r, IReadOnlyDictionary<int, string> kindCodes) => new(
        r.RuleCode,
        kindCodes[r.EventKindId],
        r.Language,
        JsonSerializer.Deserialize<List<PatternDefinition>>(r.PositivePatterns.RootElement.GetRawText(), Json) ?? [],
        r.NegativePatterns is null ? null : JsonSerializer.Deserialize<List<PatternDefinition>>(r.NegativePatterns.RootElement.GetRawText(), Json),
        r.Priority,
        r.SourceScope is { } scope && scope.RootElement.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null,
        r.ExtractionHints is null ? null : r.ExtractionHints.RootElement.Clone(),
        r.ConfidenceModifier,
        r.RuleVersion,
        r.Enabled);
}
