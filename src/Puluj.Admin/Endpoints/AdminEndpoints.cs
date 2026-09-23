using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Puluj.Contracts;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;
using Puluj.Processing.Llm;

namespace Puluj.Admin;

/// <summary>
/// Settings page backend. Protected by the `Admin:Token` setting (header X-Admin-Token); while no token is configured,
/// only requests from localhost are accepted so a fresh install can be set up from the same machine.
/// </summary>
public static class AdminEndpoints
{
    public const string TokenHeader = "X-Admin-Token";

    private static readonly string[] AlertsKeys = ["Collectors:AlertsInUa:Enabled", "Collectors:AlertsInUa:Token"];
    private static readonly string[] TelegramKeys =
    [
        "Collectors:Telegram:Enabled", "Collectors:Telegram:ApiId", "Collectors:Telegram:ApiHash", "Collectors:Telegram:Phone",
        "Collectors:Telegram:Password", "Collectors:Telegram:AutoJoin", "Collectors:Telegram:BackfillLimit", "Collectors:Telegram:BackfillSince",
        "Collectors:Telegram:HistoryWorkers", "Collectors:Telegram:RpcTimeout", "Collectors:Telegram:HistoryRequestInterval", "Collectors:Telegram:HistoryMinimumInterval", "Collectors:Telegram:HistoryMaximumInterval",
    ];
    private static readonly string[] TelegramIntervalKeys = ["Collectors:Telegram:RpcTimeout", "Collectors:Telegram:HistoryRequestInterval", "Collectors:Telegram:HistoryMinimumInterval", "Collectors:Telegram:HistoryMaximumInterval"];
    private static readonly IReadOnlyDictionary<string, (double Min, double Max)> CorrelationRanges = new Dictionary<string, (double, double)>
    {
        ["Correlation:AttachThreshold"] = (0, 1),
        ["Correlation:CandidateWindowMinutes"] = (1, 24 * 60),
        ["Correlation:AmbiguityMargin"] = (0, 1),
        ["Correlation:SlackKm"] = (0, 200),
        ["Correlation:CoarseLocationAccuracyKm"] = (1, 500),
    };
    private static readonly string[] OtherKeys = ["Correlation:AttachThreshold", "Correlation:CandidateWindowMinutes", "Correlation:AmbiguityMargin", "Correlation:SlackKm", "Correlation:CoarseLocationAccuracyKm", "Admin:Token"];

    /// <summary>Defaults baked into the option classes, shown when neither the DB nor configuration sets the key.</summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Collectors:AlertsInUa:Enabled"] = "false",
        ["Collectors:Telegram:Enabled"] = "false",
        ["Collectors:Telegram:AutoJoin"] = "true",
        ["Collectors:Telegram:BackfillLimit"] = "30",
        ["Collectors:Telegram:HistoryWorkers"] = "2",
        ["Collectors:Telegram:RpcTimeout"] = "00:00:30",
        ["Collectors:Telegram:HistoryRequestInterval"] = "00:00:00.500",
        ["Collectors:Telegram:HistoryMinimumInterval"] = "00:00:00.500",
        ["Collectors:Telegram:HistoryMaximumInterval"] = "00:00:08",
        ["Llm:Enabled"] = "false",
        ["Llm:Provider"] = "Anthropic",
        ["Llm:TimeoutSeconds"] = "20",
        ["Llm:Anthropic:Model"] = "claude-opus-5",
        ["Llm:Anthropic:InputUsdPerMillionTokens"] = "5",
        ["Llm:Anthropic:OutputUsdPerMillionTokens"] = "25",
        ["Llm:Anthropic:CacheWriteUsdPerMillionTokens"] = "6.25",
        ["Llm:Anthropic:CacheReadUsdPerMillionTokens"] = "0.5",
        ["Llm:OpenAI:Model"] = "gpt-5-mini",
        ["Llm:OpenAI:BaseUrl"] = "https://api.openai.com/v1",
        ["Llm:OpenAI:InputUsdPerMillionTokens"] = "0",
        ["Llm:OpenAI:OutputUsdPerMillionTokens"] = "0",
        ["Llm:OpenAI:CacheWriteUsdPerMillionTokens"] = "0",
        ["Llm:OpenAI:CacheReadUsdPerMillionTokens"] = "0",
        ["Llm:Ollama:Model"] = "qwen3:8b",
        ["Llm:Ollama:BaseUrl"] = "http://localhost:11434/v1",
        ["Correlation:AttachThreshold"] = "0.6",
        ["Correlation:CandidateWindowMinutes"] = "120",
        ["Correlation:AmbiguityMargin"] = "0.05",
        ["Correlation:SlackKm"] = "8",
        ["Correlation:CoarseLocationAccuracyKm"] = "80",
    };

    /// <summary>Llm:Anthropic:ApiKey → ANTHROPIC_API_KEY, Llm:OpenAI:ApiKey → OPENAI_API_KEY: what the key falls back to.</summary>
    private static string? ApiKeyVariable(string key) =>
        key.EndsWith(":ApiKey", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<LlmProvider>(key.Split(':')[1], true, out var provider)
            ? LlmOptions.ApiKeyVariable(provider)
            : null;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(AuthorizeAsync);

        admin.MapGet("/settings", async (IConfiguration config, SettingsStore store, CancellationToken ct) =>
        {
            var db = await store.GetAllAsync(ct);
            return AlertsKeys.Concat(TelegramKeys).Concat(SettingsStore.LlmKeys).Concat(OtherKeys).Select(key =>
            {
                var effective = config[key];
                if (string.IsNullOrEmpty(effective) && ApiKeyVariable(key) is { } variable)
                {
                    effective = Environment.GetEnvironmentVariable(variable);
                }
                var secret = SettingsStore.SecretKeys.Contains(key);
                var source = db.ContainsKey(key) ? "db" : !string.IsNullOrEmpty(effective) ? "config" : Defaults.ContainsKey(key) ? "default" : "none";
                if (string.IsNullOrEmpty(effective) && Defaults.TryGetValue(key, out var def))
                {
                    effective = def;
                }
                return new SettingDto(key, secret ? null : effective, secret, !string.IsNullOrEmpty(effective), source);
            }).ToList();
        });

        admin.MapPut("/settings", async (SettingsUpdateRequest req, IConfiguration config, SettingsStore store, CancellationToken ct) =>
        {
            var unknown = req.Values.Keys.Where(k => !SettingsStore.EditableKeys.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                return Results.BadRequest(new { error = $"keys not editable: {string.Join(", ", unknown)}" });
            }
            if (req.Values.TryGetValue("Collectors:Telegram:ApiHash", out var hash) && !string.IsNullOrEmpty(hash) && !Puluj.Collectors.Telegram.TelegramCollector.IsValidApiHash(hash.Trim()))
            {
                return Results.BadRequest(new { error = "api_hash має бути рядком із 32 hex-символів (0-9, a-f) з my.telegram.org" });
            }
            if (req.Values.TryGetValue("Collectors:Telegram:ApiId", out var apiId) && !string.IsNullOrEmpty(apiId) && !int.TryParse(apiId.Trim(), out _))
            {
                return Results.BadRequest(new { error = "api_id має бути числом" });
            }
            if (req.Values.TryGetValue("Collectors:Telegram:HistoryWorkers", out var workers) && !string.IsNullOrWhiteSpace(workers) && (!int.TryParse(workers, out var parsedWorkers) || parsedWorkers is < 1 or > 2))
            {
                return Results.BadRequest(new { error = "History workers має бути числом від 1 до 2." });
            }
            var telegramIntervals = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in TelegramIntervalKeys)
            {
                var value = req.Values.TryGetValue(key, out var requested) && !string.IsNullOrWhiteSpace(requested)
                    ? requested
                    : config[key] ?? Defaults[key];
                if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var interval) || interval <= TimeSpan.Zero)
                {
                    return Results.BadRequest(new { error = $"{key} має бути додатнім TimeSpan, наприклад 00:00:30." });
                }
                telegramIntervals[key] = interval;
            }
            var minimum = telegramIntervals["Collectors:Telegram:HistoryMinimumInterval"];
            var initial = telegramIntervals["Collectors:Telegram:HistoryRequestInterval"];
            var maximum = telegramIntervals["Collectors:Telegram:HistoryMaximumInterval"];
            if (minimum > maximum || initial < minimum || initial > maximum)
            {
                return Results.BadRequest(new { error = "Telegram history intervals мають відповідати правилу: minimum ≤ request ≤ maximum." });
            }
            if (req.Values.TryGetValue("Llm:Provider", out var provider) && !string.IsNullOrWhiteSpace(provider) && !new LlmOptions { Provider = provider }.TryGetProvider(out _))
            {
                return Results.BadRequest(new { error = "Llm:Provider має бути одним із: Anthropic, OpenAI, Ollama." });
            }
            foreach (var (key, value) in req.Values.Where(x => x.Key.StartsWith("Llm:", StringComparison.OrdinalIgnoreCase) && x.Key.EndsWith(":BaseUrl", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value)))
            {
                if (!(Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https"))
                {
                    return Results.BadRequest(new { error = $"{key} має бути абсолютною http(s)-адресою, наприклад http://host.docker.internal:11434/v1." });
                }
            }
            if (req.Values.TryGetValue("Llm:TimeoutSeconds", out var llmTimeout) && !string.IsNullOrWhiteSpace(llmTimeout) && (!int.TryParse(llmTimeout, out var seconds) || seconds is < 1 or > 600))
            {
                return Results.BadRequest(new { error = "Llm:TimeoutSeconds має бути числом від 1 до 600." });
            }
            foreach (var (key, value) in req.Values.Where(x => x.Key.StartsWith("Llm:", StringComparison.OrdinalIgnoreCase) && x.Key.EndsWith("UsdPerMillionTokens", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value)))
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price < 0)
                {
                    return Results.BadRequest(new { error = $"{key} має бути невід'ємним числом у USD за мільйон токенів." });
                }
            }
            foreach (var (key, value) in req.Values.Where(x => CorrelationRanges.ContainsKey(x.Key) && !string.IsNullOrWhiteSpace(x.Value)))
            {
                var range = CorrelationRanges[key];
                if (!double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number < range.Min || number > range.Max)
                {
                    return Results.BadRequest(new { error = $"{key} має бути числом від {range.Min} до {range.Max}." });
                }
            }
            var values = req.Values.ToDictionary(kv => kv.Key, kv => kv.Value?.Trim());
            await store.SetAsync(values, ct);
            return Results.Ok(new { saved = values.Count });
        });

        admin.MapGet("/status", async (IConfiguration config, SettingsStore store, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct) =>
        {
            var db = await store.GetAllAsync(ct);
            var alertsToken = await AlertsTokenAsync(factory, config, ct);
            // Alive = every known Worker instance reported within 90 s; the oldest heartbeat is the one shown.
            var now = clock.GetUtcNow();
            var workers = OpsEndpoints.WorkerHeartbeats(db, now);
            var heartbeat = workers.Count > 0 ? workers[0].At : (DateTimeOffset?)null;
            var llm = config.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions();
            var llmReady = llm.TryGetProvider(out var llmProvider)
                           && (!LlmOptions.RequiresApiKey(llmProvider) || !string.IsNullOrEmpty(llm.ApiKeyFor(llmProvider)))
                           && !string.IsNullOrWhiteSpace(llm.Current.Model);
            return new AdminStatusDto(
                AlertsConfigured: config.GetValue<bool>("Collectors:AlertsInUa:Enabled") && !string.IsNullOrEmpty(alertsToken),
                TelegramConfigured: config.GetValue<bool>("Collectors:Telegram:Enabled") && config.GetValue<int>("Collectors:Telegram:ApiId") != 0
                                    && !string.IsNullOrEmpty(config["Collectors:Telegram:ApiHash"]) && !string.IsNullOrEmpty(config["Collectors:Telegram:Phone"]),
                LlmConfigured: config.GetValue<bool>("Llm:Enabled") && llmReady,
                TelegramStatus: db.TryGetValue("Runtime:Telegram:Status", out var ts) ? ts.Value : null,
                AdminTokenSet: !string.IsNullOrEmpty(config["Admin:Token"]),
                WorkerAlive: heartbeat is not null && now - heartbeat.Value < TimeSpan.FromSeconds(90),
                WorkerLastSeen: heartbeat);
        });

        admin.MapGet("/sources", async (IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Sources.AsNoTracking().Include(s => s.CollectorState).OrderByDescending(s => s.Priority).ThenBy(s => s.Name).ToListAsync(ct);
            var counts = await db.RawMessages.GroupBy(r => r.SourceId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var telegram = await LatestTelegramInfoAsync(db, ct);
            var now = clock.GetUtcNow();
            return rows.Select(s => ToDto(s, now, counts.GetValueOrDefault(s.SourceId), telegram.GetValueOrDefault(s.SourceId))).ToList();
        });

        admin.MapPut("/sources/{id:int}", async (int id, SourceUpdateRequest req, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var s = await db.Sources.Include(x => x.CollectorState).FirstOrDefaultAsync(x => x.SourceId == id, ct);
            if (s is null)
            {
                return Results.NotFound();
            }
            if (req.Enabled is bool e)
            {
                s.Enabled = e;
            }
            if (req.TrustLevel is double t)
            {
                s.TrustLevel = Math.Clamp(t, 0, 1);
            }
            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                s.Name = req.Name.Trim();
            }
            if (req.Priority is int p)
            {
                s.Priority = p;
            }
            if (req.PollingIntervalSeconds is int sec)
            {
                s.PollingInterval = sec <= 0 ? null : TimeSpan.FromSeconds(Math.Max(10, sec));
            }
            if (req.Url is not null)
            {
                s.Url = string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim();
            }
            if (req.Channel is not null || req.HomeRegion is not null)
            {
                var cfg = s.Config is null ? new JsonObject() : JsonNode.Parse(s.Config.RootElement.GetRawText())!.AsObject();
                if (req.Channel is not null)
                {
                    var channel = SourceCodes.NormalizeTelegramUsername(req.Channel);
                    // Moving a source onto a channel another source already collects would duplicate that channel.
                    if (s.Type == SourceType.Telegram && await FindTelegramChannelAsync(db, channel, exceptId: id, ct) is { } dup)
                    {
                        return Results.Conflict(new { error = $"канал @{channel} уже є джерелом '{dup.Code}'" });
                    }
                    cfg["channel"] = channel ?? "";
                    if (s.Type == SourceType.Telegram && string.IsNullOrWhiteSpace(req.Url))
                    {
                        s.Url = $"https://t.me/{cfg["channel"]}";
                    }
                }
                if (req.HomeRegion is not null)
                {
                    if (string.IsNullOrWhiteSpace(req.HomeRegion))
                    {
                        cfg.Remove("homeRegion");
                    }
                    else
                    {
                        cfg["homeRegion"] = req.HomeRegion.Trim();
                    }
                }
                s.Config = JsonDocument.Parse(cfg.ToJsonString());
            }
            if (req.Token is not null)
            {
                var secrets = s.Secrets is null ? new JsonObject() : JsonNode.Parse(s.Secrets.RootElement.GetRawText())!.AsObject();
                if (string.IsNullOrWhiteSpace(req.Token))
                {
                    secrets.Remove("token");
                }
                else
                {
                    secrets["token"] = req.Token.Trim();
                }
                s.Secrets = secrets.Count == 0 ? null : JsonDocument.Parse(secrets.ToJsonString());
            }
            await db.SaveChangesAsync(ct);
            var count = await db.RawMessages.LongCountAsync(r => r.SourceId == id, ct);
            var telegram = await LatestTelegramInfoAsync(db, ct);
            return Results.Ok(ToDto(s, clock.GetUtcNow(), count, telegram.GetValueOrDefault(s.SourceId)));
        });

        admin.MapPost("/sources", async (SourceCreateRequest req, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SourceType>(req.Type, true, out var type))
            {
                return Results.BadRequest(new { error = "type must be Telegram|RestApi|Rss|Web" });
            }
            var channel = SourceCodes.NormalizeTelegramUsername(req.Channel);
            if (type == SourceType.Telegram && string.IsNullOrEmpty(channel))
            {
                return Results.BadRequest(new { error = "для Telegram потрібен username каналу" });
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            // Codes follow docs/naming.md ("Коди джерел"): tg_<username lowercase> for Telegram, so one channel always
            // maps to one code.
            var code = type == SourceType.Telegram ? SourceCodes.Telegram(channel!) : $"{type.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}"[..16];
            if (await db.Sources.AnyAsync(x => x.Code == code, ct))
            {
                return Results.Conflict(new { error = $"джерело '{code}' уже існує" });
            }
            // A legacy code (seeded before the convention) may already cover this channel: one channel, one source, or
            // the collector reads it twice. Case-insensitive on the username, like Telegram itself.
            if (type == SourceType.Telegram && await FindTelegramChannelAsync(db, channel, exceptId: null, ct) is { } dup)
            {
                return Results.Conflict(new { error = $"канал @{channel} уже є джерелом '{dup.Code}'" });
            }
            var s = new Source
            {
                Code = code,
                Name = string.IsNullOrWhiteSpace(req.Name) ? (channel ?? code) : req.Name.Trim(),
                Type = type,
                Url = req.Url ?? (type == SourceType.Telegram ? $"https://t.me/{channel}" : null),
                TrustLevel = Math.Clamp(req.TrustLevel ?? 0.6, 0, 1),
                Priority = req.Priority ?? 50,
                Enabled = true,
                PollingInterval = req.PollingIntervalSeconds is int sec and > 0 ? TimeSpan.FromSeconds(Math.Max(10, sec)) : null,
                Config = JsonDocument.Parse(JsonSerializer.Serialize(new { channel, language = "uk", official = false })),
            };
            db.Sources.Add(s);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/sources/{s.SourceId}", ToDto(s, clock.GetUtcNow(), 0));
        });

        // Sources with stored messages are part of the provenance chain and cannot be deleted — disable them instead.
        admin.MapDelete("/sources/{id:int}", async (int id, IDbContextFactory<PulujDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var s = await db.Sources.Include(x => x.CollectorState).FirstOrDefaultAsync(x => x.SourceId == id, ct);
            if (s is null)
            {
                return Results.NotFound();
            }
            if (await db.RawMessages.AnyAsync(r => r.SourceId == id, ct))
            {
                return Results.Conflict(new { error = "джерело має збережені повідомлення; вимкніть його замість видалення" });
            }
            if (s.CollectorState is not null)
            {
                db.CollectorStates.Remove(s.CollectorState);
            }
            db.Sources.Remove(s);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapPost("/telegram/code", async (TelegramCodeRequest req, SettingsStore store, CancellationToken ct) =>
        {
            await store.SetAsync(new Dictionary<string, string?> { ["Collectors:Telegram:VerificationCode"] = req.Code.Trim() }, ct);
            return Results.NoContent();
        });

        // Quick credential check without waiting for the Worker: one request to alerts.in.ua with the stored or supplied token.
        admin.MapPost("/test/alerts", async (IConfiguration config, IHttpClientFactory http, IDbContextFactory<PulujDbContext> factory, HttpContext ctx, CancellationToken ct) =>
        {
            var token = ctx.Request.Headers["X-Test-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                token = await AlertsTokenAsync(factory, config, ct);
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                return new TestResultDto(false, "Токен не задано");
            }
            try
            {
                var client = http.CreateClient("admin-test");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = await client.GetAsync("https://api.alerts.in.ua/v1/alerts/active.json", ct);
                if (!res.IsSuccessStatusCode)
                {
                    return new TestResultDto(false, $"HTTP {(int)res.StatusCode}: {(res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "токен відхилено" : res.ReasonPhrase)}");
                }
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var count = doc.RootElement.TryGetProperty("alerts", out var a) ? a.GetArrayLength() : 0;
                return new TestResultDto(true, $"OK, активних тривог зараз: {count}");
            }
            catch (Exception ex)
            {
                return new TestResultDto(false, ex.Message);
            }
        });

        return app;
    }

    internal static async ValueTask<object?> AuthorizeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var config = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var token = config["Admin:Token"];
        if (string.IsNullOrEmpty(token))
        {
            var ip = ctx.HttpContext.Connection.RemoteIpAddress;
            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
            {
                return Results.Json(new { error = "admin token not configured: settings are available from localhost only" }, statusCode: 403);
            }
            return await next(ctx);
        }
        var supplied = ctx.HttpContext.Request.Headers[TokenHeader].FirstOrDefault();
        return supplied == token ? await next(ctx) : Results.Json(new { error = "invalid admin token" }, statusCode: 401);
    }

    /// <summary>The alerts.in.ua token: the one stored on the alerts source, else the configuration / env fallback.</summary>
    private static async Task<string?> AlertsTokenAsync(IDbContextFactory<PulujDbContext> factory, IConfiguration config, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Code == Puluj.Collectors.AlertsInUa.AlertsInUaCollector.CollectorCode, ct);
        return source?.Secret("token") is { Length: > 0 } t ? t : config["Collectors:AlertsInUa:Token"];
    }

    internal sealed record TelegramChannelInfo(int SourceId, string? ChannelTitle, int? SubscriberCount);

    /// <summary>The latest channel metadata observed in a Telegram post.  It is evidence from collection time,
    /// not a fresh network lookup, so the admin UI never adds Telegram API traffic.</summary>
    internal static async Task<IReadOnlyDictionary<int, TelegramChannelInfo>> LatestTelegramInfoAsync(PulujDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<TelegramChannelInfo>($"""
            SELECT s.source_id AS source_id,
                   r.raw_payload ->> 'channelTitle' AS channel_title,
                   NULLIF(r.raw_payload ->> 'subscriberCount', '')::int AS subscriber_count
            FROM sources s
            CROSS JOIN LATERAL (
                SELECT r.raw_payload
                FROM raw_messages r
                WHERE r.source_id = s.source_id
                  AND r.raw_payload IS NOT NULL
                  AND (r.raw_payload ? 'channelTitle' OR r.raw_payload ? 'subscriberCount')
                ORDER BY r.received_at DESC, r.raw_message_id DESC
                LIMIT 1) r
            WHERE s.type = {(int)SourceType.Telegram}
            """).ToListAsync(ct);
        return rows.ToDictionary(x => x.SourceId);
    }

    /// <summary>
    /// The Telegram source that already collects <paramref name="channel"/> (any code, username case-insensitive), or null.
    /// The channel lives in jsonb config, so the few Telegram rows are compared in memory rather than with an expression index.
    /// </summary>
    private static async Task<Source?> FindTelegramChannelAsync(PulujDbContext db, string? channel, int? exceptId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(channel))
        {
            return null;
        }
        var telegram = await db.Sources.AsNoTracking().Where(x => x.Type == SourceType.Telegram && x.SourceId != exceptId).ToListAsync(ct);
        return SourceCodes.FindTelegramChannel(telegram, channel);
    }

    private static AdminSourceDto ToDto(Source s, DateTimeOffset now, long rawCount, TelegramChannelInfo? telegram = null)
    {
        var st = s.CollectorState;
        var interval = s.PollingInterval ?? TimeSpan.FromMinutes(5);
        var status = !s.Enabled ? "disabled"
            : st is null ? "idle"
            : st.LastSuccessAt is null || now - st.LastSuccessAt.Value > interval * 3 ? "stale"
            : "ok";
        string? Str(string name) => s.Config is not null && s.Config.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new AdminSourceDto(s.SourceId, s.Code, s.Name, s.Type.ToString(), s.Enabled, s.TrustLevel, s.Priority, s.Url, Str("channel"),
            s.PollingInterval is { } pi ? (int)pi.TotalSeconds : null, Str("homeRegion"), !string.IsNullOrEmpty(s.Secret("token")), rawCount,
            st?.LastSuccessAt, st?.LastMessageAt, st?.ConsecutiveFailures ?? 0, st?.LastError, status, telegram?.ChannelTitle, telegram?.SubscriberCount);
    }
}
