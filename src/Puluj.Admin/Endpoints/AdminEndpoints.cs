using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

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
        "Collectors:Telegram:Password", "Collectors:Telegram:AutoJoin", "Collectors:Telegram:BackfillLimit",
    ];
    private static readonly string[] LlmKeys = ["Llm:Enabled", "Llm:Model", "Llm:ApiKey", "Llm:InputUsdPerMillionTokens", "Llm:OutputUsdPerMillionTokens", "Llm:CacheWriteUsdPerMillionTokens", "Llm:CacheReadUsdPerMillionTokens"];
    private static readonly HashSet<string> LlmPriceKeys = ["Llm:InputUsdPerMillionTokens", "Llm:OutputUsdPerMillionTokens", "Llm:CacheWriteUsdPerMillionTokens", "Llm:CacheReadUsdPerMillionTokens"];
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
        ["Llm:Enabled"] = "false",
        ["Llm:Model"] = "claude-opus-5",
        ["Llm:InputUsdPerMillionTokens"] = "5",
        ["Llm:OutputUsdPerMillionTokens"] = "25",
        ["Llm:CacheWriteUsdPerMillionTokens"] = "6.25",
        ["Llm:CacheReadUsdPerMillionTokens"] = "0.5",
        ["Correlation:AttachThreshold"] = "0.6",
        ["Correlation:CandidateWindowMinutes"] = "120",
        ["Correlation:AmbiguityMargin"] = "0.05",
        ["Correlation:SlackKm"] = "8",
        ["Correlation:CoarseLocationAccuracyKm"] = "80",
    };

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(AuthorizeAsync);

        admin.MapGet("/settings", async (IConfiguration config, SettingsStore store, CancellationToken ct) =>
        {
            var db = await store.GetAllAsync(ct);
            return AlertsKeys.Concat(TelegramKeys).Concat(LlmKeys).Concat(OtherKeys).Select(key =>
            {
                var effective = config[key];
                if (key == "Llm:ApiKey" && string.IsNullOrEmpty(effective))
                {
                    effective = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
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

        admin.MapPut("/settings", async (SettingsUpdateRequest req, SettingsStore store, CancellationToken ct) =>
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
            foreach (var (key, value) in req.Values.Where(x => LlmPriceKeys.Contains(x.Key) && !string.IsNullOrWhiteSpace(x.Value)))
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
            var llmKey = config["Llm:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return new AdminStatusDto(
                AlertsConfigured: config.GetValue<bool>("Collectors:AlertsInUa:Enabled") && !string.IsNullOrEmpty(alertsToken),
                TelegramConfigured: config.GetValue<bool>("Collectors:Telegram:Enabled") && config.GetValue<int>("Collectors:Telegram:ApiId") != 0
                                    && !string.IsNullOrEmpty(config["Collectors:Telegram:ApiHash"]) && !string.IsNullOrEmpty(config["Collectors:Telegram:Phone"]),
                LlmConfigured: config.GetValue<bool>("Llm:Enabled") && !string.IsNullOrEmpty(llmKey),
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
            var now = clock.GetUtcNow();
            return rows.Select(s => ToDto(s, now, counts.GetValueOrDefault(s.SourceId))).ToList();
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
                    cfg["channel"] = Puluj.Collectors.Telegram.TelegramCollector.NormalizeUsername(req.Channel) ?? "";
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
            return Results.Ok(ToDto(s, clock.GetUtcNow(), count));
        });

        admin.MapPost("/sources", async (SourceCreateRequest req, IDbContextFactory<PulujDbContext> factory, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SourceType>(req.Type, true, out var type))
            {
                return Results.BadRequest(new { error = "type must be Telegram|RestApi|Rss|Web" });
            }
            var channel = Puluj.Collectors.Telegram.TelegramCollector.NormalizeUsername(req.Channel);
            if (type == SourceType.Telegram && string.IsNullOrEmpty(channel))
            {
                return Results.BadRequest(new { error = "для Telegram потрібен username каналу" });
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            var code = type == SourceType.Telegram ? $"tg_{channel!.ToLowerInvariant()}" : $"{type.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}"[..16];
            if (await db.Sources.AnyAsync(x => x.Code == code, ct))
            {
                return Results.Conflict(new { error = $"джерело '{code}' уже існує" });
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

    private static AdminSourceDto ToDto(Source s, DateTimeOffset now, long rawCount)
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
            st?.LastSuccessAt, st?.LastMessageAt, st?.ConsecutiveFailures ?? 0, st?.LastError, status);
    }
}
