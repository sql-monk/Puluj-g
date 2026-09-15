using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Enums;
using Puluj.Domain.Entities;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Processing.Llm;

/// <summary>
/// Fallback parser (spec §4 "Parser / NLP", plan §3 p.3): the rule parser runs first; the model is asked only when
/// rules found nothing in a message that still looks like a target report. The model must answer in a fixed JSON
/// schema, may only use taxonomy codes it is given, and is told to leave fields null rather than guess (spec §9).
/// </summary>
public sealed class LlmParser : IParser
{
    private static readonly string[] TriggerStems =
        ["загроз", "курс", "напрям", "рухає", "летить", "летять", "зафіксов", "укритт", "небезпек", "тривог", "пуск", "зліт", "ціл", "ппо", "вибух", "ракет", "дрон", "бпла"];

    private readonly RuleParser _rules;
    private readonly IIndexes _indexes;
    private readonly INormalizer _normalizer;
    private readonly IOptionsMonitor<LlmOptions> _monitor;
    private readonly PulujMetrics _metrics;
    private readonly ILogger<LlmParser> _logger;
    private readonly RateLimiter _limiter;
    private readonly LlmBreaker _breaker;
    private readonly TimeProvider _clock;
    private readonly Lazy<string> _systemPrompt;
    private readonly IDbContextFactory<PulujDbContext>? _auditFactory;
    private readonly string _workerName;
    private readonly object _clientLock = new();
    private AnthropicClient? _client;
    private string? _clientKey;
    private bool _warnedNoKey;

    public LlmParser(RuleParser rules, IIndexes indexes, INormalizer normalizer, IOptionsMonitor<LlmOptions> options, LlmBreaker breaker, PulujMetrics metrics, TimeProvider clock, ILogger<LlmParser> logger,
        IDbContextFactory<PulujDbContext>? auditFactory = null, ProcessorIdentity? identity = null)
    {
        _rules = rules;
        _clock = clock;
        _breaker = breaker;
        _indexes = indexes;
        _normalizer = normalizer;
        _monitor = options;
        _metrics = metrics;
        _logger = logger;
        _auditFactory = auditFactory;
        _workerName = identity?.Name ?? Environment.MachineName;
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.CurrentValue.MaxCallsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
        _systemPrompt = new Lazy<string>(BuildSystemPrompt);
    }

    private LlmOptions _options => _monitor.CurrentValue;

    public string Version => $"llm-{_options.Model}-p{_options.PromptVersion}";

    /// <summary>Client is (re)created when the key changes — the admin UI can enable the fallback at runtime.</summary>
    private AnthropicClient? Client
    {
        get
        {
            var o = _options;
            if (!o.Enabled)
            {
                return null;
            }
            var key = string.IsNullOrWhiteSpace(o.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : o.ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                if (!_warnedNoKey)
                {
                    _warnedNoKey = true;
                    _logger.LogWarning("Llm:Enabled is true but no API key (Llm:ApiKey / ANTHROPIC_API_KEY); LLM fallback disabled");
                }
                return null;
            }
            lock (_clientLock)
            {
                if (_client is null || _clientKey != key)
                {
                    _client = new AnthropicClient { ApiKey = key };
                    _clientKey = key;
                }
                return _client;
            }
        }
    }

    public async Task<IReadOnlyList<ParsedFact>> ParseAsync(NormalizedMessage message, ParseContext ctx, CancellationToken ct)
    {
        var facts = _rules.Parse(message, ctx);
        if (facts.Count > 0 || Client is null || !LooksLikeTargetReport(message))
        {
            return facts;
        }
        // History (a rebuild, a backfill) stays with the rules: the model is for the live picture, not for years of archive.
        var maxAge = _options.MaxMessageAgeHours;
        if (maxAge > 0 && ctx.PublishedAt is { } publishedAt && DateTimeOffset.UtcNow - publishedAt > TimeSpan.FromHours(maxAge))
        {
            _metrics.LlmCall("stale");
            return facts;
        }
        if (_breaker.IsOpen(_clock.GetUtcNow(), out var reason))
        {
            _metrics.LlmCall("paused");
            _logger.LogDebug("LLM paused ({Reason}); rules only", reason);
            return facts;
        }
        using var lease = _limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            _metrics.LlmCall("rate_limited");
            return facts;
        }
        _breaker.Attempt();
        var started = _clock.GetUtcNow();
        try
        {
            var answer = await AskAsync(message.Text, ct);
            var mapped = Map(answer.Result, message);
            _breaker.Reset();
            _metrics.LlmCall(mapped.Count > 0 ? "facts" : "empty");
            await AuditAsync(ctx, message.Text, answer, answer.Refused ? "refusal" : mapped.Count > 0 ? "facts" : "empty", null, null, mapped.Count, started, ct);
            return mapped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AnthropicApiException ex)
        {
            _metrics.LlmCall(ex is AnthropicRateLimitException ? "429" : "api_error");
            // The API's own message (billing, auth, a rejected schema) says it all; the stack trace would only repeat the SDK.
            var detail = ErrorMessage(ex);
            await AuditAsync(ctx, message.Text, null, ex is AnthropicRateLimitException ? "429" : "api_error", (int)ex.StatusCode, detail, 0, started, ct);
            if (_breaker.Trip(ex.StatusCode, detail, _clock.GetUtcNow()) is { } pause)
            {
                _logger.LogWarning("LLM request failed with {Status}: {Detail}; model paused for {Pause}", (int)ex.StatusCode, detail, pause);
            }
            else
            {
                _logger.LogWarning("LLM request failed with {Status}: {Detail}", (int)ex.StatusCode, detail);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or JsonException)
        {
            _metrics.LlmCall("error");
            _breaker.Fail();
            await AuditAsync(ctx, message.Text, null, "error", null, ex.Message, 0, started, ct);
            _logger.LogWarning(ex, "LLM fallback failed");
        }
        return facts;
    }

    /// <summary>The API's error message out of the response body ({"type":"error","error":{"type":..,"message":..}}), else the exception's.</summary>
    private static string ErrorMessage(AnthropicApiException ex)
    {
        try
        {
            using var doc = JsonDocument.Parse(ex.ResponseBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (message is not null)
                {
                    return type is null ? message : $"{type}: {message}";
                }
            }
        }
        catch (JsonException)
        {
        }
        return ex.Message;
    }

    private static bool LooksLikeTargetReport(NormalizedMessage message) =>
        message.Segments.SelectMany(s => s.Tokens).Any(t => TriggerStems.Any(stem => t.Text.StartsWith(stem, StringComparison.Ordinal)));

    private async Task<LlmAnswer> AskAsync(string text, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var response = await Client!.Messages.Create(new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = 2048,
            System = new List<TextBlockParam> { new() { Text = _systemPrompt.Value, CacheControl = new CacheControlEphemeral() } },
            OutputConfig = new OutputConfig
            {
                Effort = Effort.Low,
                Format = new JsonOutputFormat { Schema = Schema() },
            },
            Messages = [new() { Role = Role.User, Content = text }],
        }, cancellationToken: timeout.Token);

        if (response.StopReason == "refusal")
        {
            _logger.LogInformation("LLM declined to classify the message");
            return new LlmAnswer(new LlmResponse([]), null, true, response.Usage.InputTokens, response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0, response.Usage.OutputTokens);
        }
        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));
        return new LlmAnswer(JsonSerializer.Deserialize<LlmResponse>(json, JsonOptions) ?? new LlmResponse([]), json, false,
            response.Usage.InputTokens, response.Usage.CacheCreationInputTokens ?? 0, response.Usage.CacheReadInputTokens ?? 0, response.Usage.OutputTokens);
    }

    /// <summary>Audit must never make an otherwise usable parsing result fail. A cost is an estimate from the exact
    /// provider usage object and the price card active at this instant, kept with the row for historical accuracy.</summary>
    private async Task AuditAsync(ParseContext ctx, string requestText, LlmAnswer? answer, string outcome, int? statusCode, string? error, int factsCount, DateTimeOffset started, CancellationToken ct)
    {
        if (_auditFactory is null)
        {
            return;
        }
        try
        {
            var o = _options;
            var input = answer?.InputTokens;
            var cacheWrite = answer?.CacheCreationInputTokens;
            var cacheRead = answer?.CacheReadInputTokens;
            var output = answer?.OutputTokens;
            decimal? cost = input is null ? null : Math.Round(
                (input.Value * o.InputUsdPerMillionTokens + cacheWrite!.Value * o.CacheWriteUsdPerMillionTokens + cacheRead!.Value * o.CacheReadUsdPerMillionTokens + output!.Value * o.OutputUsdPerMillionTokens) / 1_000_000m,
                9, MidpointRounding.AwayFromZero);
            await using var db = await _auditFactory.CreateDbContextAsync(ct);
            db.LlmRequests.Add(new LlmRequest
            {
                RawMessageId = ctx.RawMessageId,
                SourceId = ctx.SourceId,
                OccurredAt = _clock.GetUtcNow(),
                Worker = _workerName,
                Model = o.Model,
                PromptVersion = o.PromptVersion,
                Outcome = outcome,
                StatusCode = statusCode,
                DurationMs = (int)Math.Min((_clock.GetUtcNow() - started).TotalMilliseconds, int.MaxValue),
                InputTokens = input,
                CacheCreationInputTokens = cacheWrite,
                CacheReadInputTokens = cacheRead,
                OutputTokens = output,
                EstimatedCostUsd = cost,
                FactsCount = factsCount,
                RequestText = requestText,
                SystemPrompt = _systemPrompt.Value,
                ResponseText = answer?.ResponseText,
                Error = error,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not write LLM audit row");
        }
    }

    /// <summary>Maps a raw model answer (JSON per <see cref="Schema"/>) to facts. Internal so the mapping is testable without an API key.</summary>
    internal IReadOnlyList<ParsedFact> MapJson(string json, NormalizedMessage message) =>
        Map(JsonSerializer.Deserialize<LlmResponse>(json, JsonOptions) ?? new LlmResponse([]), message);

    private IReadOnlyList<ParsedFact> Map(LlmResponse response, NormalizedMessage message)
    {
        var taxonomy = _indexes.Taxonomy;
        var placeMatcher = new PlaceMatcher(_indexes.Gazetteer);
        var facts = new List<ParsedFact>();
        foreach (var f in response.Facts ?? [])
        {
            var eventType = Enum.TryParse<EventType>(f.EventType, true, out var e) ? e : EventType.Unknown;
            TargetMention? target = null;
            if (f.Target is { Code.Length: > 0 } t && Enum.TryParse<AliasTargetLevel>(t.Level, true, out var level))
            {
                var targetRef = FindRef(taxonomy, level, t.Code);
                if (targetRef is not null)
                {
                    target = new TargetMention(targetRef, t.Code, level == AliasTargetLevel.Model ? ConfidenceLevel.Medium : ConfidenceLevel.Low, f.Hedged, 0, 0);
                }
            }
            if (target is null && eventType is EventType.Unknown or EventType.TargetObserved)
            {
                continue; // nothing verifiable
            }
            var places = new List<PlaceMention>();
            foreach (var p in f.Places ?? [])
            {
                var normalized = _normalizer.Normalize(p.Name ?? "");
                if (normalized.Segments.Count == 0)
                {
                    continue;
                }
                var match = placeMatcher.Match(normalized.Segments[0], new ParseContext(0, "uk", null), new HashSet<int>(), new HashSet<(int, int)>()).FirstOrDefault();
                if (match is null)
                {
                    continue; // unknown place names are dropped, never invented
                }
                var role = Enum.TryParse<PlaceRole>(p.Role, true, out var r) ? r : PlaceRole.Current;
                places.Add(match with { Role = role });
            }
            var segmentIndex = Math.Clamp(f.Segment ?? 0, 0, Math.Max(0, message.Segments.Count - 1));
            facts.Add(new ParsedFact
            {
                SegmentIndex = segmentIndex,
                SegmentText = f.Quote ?? (message.Segments.Count > 0 ? message.Segments[segmentIndex].Text : message.Text),
                EventType = eventType == EventType.Unknown ? EventType.TargetObserved : eventType,
                Target = target,
                Count = f.Count,
                CountIsApproximate = f.CountApprox,
                Places = places,
                Direction = f.DirectionDeg is double d ? new DirectionMention(((d % 360) + 360) % 360, DirectionKind.Compass, "llm") : null,
                IsLaunch = f.Launch,
                Rules = ["llm"],
                Method = IdentificationMethod.Llm,
                ParserVersion = Version,
            });
        }
        return facts;
    }

    private static TargetRef? FindRef(TaxonomyIndex taxonomy, AliasTargetLevel level, string code)
    {
        // Codes are unique per level; scan the alias table's resolved refs by code.
        foreach (var alias in taxonomy.Aliases)
        {
            if (alias.Level == level && taxonomy.Resolve(level, alias.TargetId) is { } r && r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }
        return level == AliasTargetLevel.Category && taxonomy.CategoryId(code) is int cat ? taxonomy.Resolve(level, cat)
            : level == AliasTargetLevel.Class && taxonomy.ClassId(code) is int cls ? taxonomy.Resolve(level, cls)
            : null;
    }

    private string BuildSystemPrompt()
    {
        var taxonomy = _indexes.Taxonomy;
        var codes = taxonomy.Aliases
            .Select(a => taxonomy.Resolve(a.Level, a.TargetId))
            .OfType<TargetRef>()
            .DistinctBy(r => (r.Level, r.Code))
            .OrderBy(r => r.Level).ThenBy(r => r.Code)
            .Select(r => $"{r.Level.ToString().ToLowerInvariant()}:{r.Code} ({r.Name})");
        var sb = new StringBuilder();
        sb.AppendLine("You extract air-target facts from Ukrainian/Russian OSINT messages (air force, regional administrations, monitoring channels) for a civil situational-awareness map.");
        sb.AppendLine("Return only what the text states. Never infer a specific missile or drone model from context: use the most generic level the text supports. If the text says 'ймовірно', 'можливо' or uses '?', set hedged=true.");
        sb.AppendLine("One fact per distinct target statement. Place names must be copied as written in the text (nominative form is fine); roles: current (where it is now), origin (where it came from, 'з ...'), destination ('курсом на ...', 'у напрямку ...'), transit ('через ...').");
        sb.AppendLine("directionDeg is a compass heading in degrees only when the text names a direction (north=0, east=90, south=180, west=270). Leave count null unless a number or group word is present (група/декілька -> 3 with countApprox=true).");
        sb.AppendLine("eventType: TargetObserved for a target report, AirRaidAlert, AlertCancelled, TargetCancelled, ExplosionReport, AirDefenseActivity. Messages that are not about air targets yield an empty facts list.");
        sb.AppendLine("Allowed target codes (level:CODE):");
        foreach (var c in codes)
        {
            sb.Append("- ").AppendLine(c);
        }
        return sb.ToString();
    }

    private static Dictionary<string, JsonElement> Schema()
    {
        var fact = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                eventType = new { type = "string", @enum = new[] { "TargetObserved", "AirRaidAlert", "AlertCancelled", "TargetCancelled", "ExplosionReport", "AirDefenseActivity" } },
                target = new
                {
                    type = new[] { "object", "null" },
                    additionalProperties = false,
                    properties = new
                    {
                        level = new { type = "string", @enum = new[] { "category", "class", "family", "model" } },
                        code = new { type = "string" },
                    },
                    required = new[] { "level", "code" },
                },
                hedged = new { type = "boolean" },
                count = new { type = new[] { "integer", "null" } },
                countApprox = new { type = "boolean" },
                places = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            name = new { type = "string" },
                            role = new { type = "string", @enum = new[] { "current", "origin", "destination", "transit" } },
                        },
                        required = new[] { "name", "role" },
                    },
                },
                directionDeg = new { type = new[] { "number", "null" } },
                launch = new { type = "boolean" },
                segment = new { type = new[] { "integer", "null" } },
                quote = new { type = new[] { "string", "null" } },
            },
            required = new[] { "eventType", "target", "hedged", "count", "countApprox", "places", "directionDeg", "launch", "segment", "quote" },
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["properties"] = JsonSerializer.SerializeToElement(new { facts = new { type = "array", items = fact } }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "facts" }),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LlmResponse(List<LlmFact>? Facts);
    private sealed record LlmAnswer(LlmResponse Result, string? ResponseText, bool Refused, long InputTokens, long CacheCreationInputTokens, long CacheReadInputTokens, long OutputTokens);
    private sealed record LlmFact(string? EventType, LlmTarget? Target, bool Hedged, int? Count, bool CountApprox, List<LlmPlace>? Places, double? DirectionDeg, bool Launch, int? Segment, string? Quote);
    private sealed record LlmTarget(string? Level, string? Code);
    private sealed record LlmPlace(string? Name, string? Role);
}
