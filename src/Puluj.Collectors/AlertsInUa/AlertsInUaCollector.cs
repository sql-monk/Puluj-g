using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Collectors.AlertsInUa;

/// <summary>
/// Polls alerts.in.ua active alerts and turns every state change into a RawMessage:
/// kind=alert.started (payload = alert object) / kind=alert.finished. The current active set is kept in
/// CollectorState.Cursor so ends that happened while the worker was down are still emitted after restart.
/// Alerts still open in the database that the feed no longer lists (injected by hand, or left behind by a rebuild)
/// get an end too, so the feed is the single authority on what is active.
/// </summary>
public sealed class AlertsInUaCollector(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<AlertsInUaOptions> options,
    CollectorIngress ingress,
    CollectorStateStore states,
    IDbContextFactory<PulujDbContext> factory,
    TimeProvider clock,
    ILogger<AlertsInUaCollector> logger) : ICollector
{
    public const string HttpClientName = "alerts.in.ua";
    public const string CollectorCode = "alerts_in_ua";

    public string Name => "alerts.in.ua";

    public bool Handles(Source source) =>
        options.CurrentValue.Enabled
        && source.Type == SourceType.RestApi
        && source.Config?.RootElement.TryGetProperty("collector", out var c) == true
        && c.GetString() == CollectorCode;

    public async Task RunAsync(IReadOnlyList<Source> sources, CancellationToken ct)
    {
        var source = sources[0];
        // The token lives on the source row (settings page); configuration / env is only the bootstrap fallback.
        var token = TokenFor(source, options.CurrentValue);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("alerts.in.ua token missing (source secret or Collectors:AlertsInUa:Token); collector idle");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        var interval = source.PollingInterval ?? options.CurrentValue.PollingInterval;
        var known = await LoadKnownAsync(source.SourceId, ct);
        logger.LogInformation("alerts.in.ua: {Count} active alerts restored from cursor", known.Count);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var active = await FetchActiveAsync(token, ct);
                known = await ReconcileAsync(source, known, active, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "alerts.in.ua poll failed");
                await states.MarkFailureAsync(source.SourceId, ex.Message, ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static string? TokenFor(Source source, AlertsInUaOptions options) =>
        source.Secret("token") is { Length: > 0 } t ? t : options.Token;

    private async Task<Dictionary<string, JsonObject>> FetchActiveAsync(string token, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(options.CurrentValue.BaseUrl);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var doc = await http.GetFromJsonAsync<JsonObject>("v1/alerts/active.json", ct)
                  ?? throw new InvalidOperationException("Empty response");
        var result = new Dictionary<string, JsonObject>();
        foreach (var node in doc["alerts"]?.AsArray() ?? [])
        {
            if (node is JsonObject alert && alert["id"] is not null)
            {
                result[alert["id"]!.ToString()] = alert;
            }
        }
        return result;
    }

    /// <summary>Ids ended because they were open in the database but absent from the feed; not repeated every poll (through the ingress the store cannot say "already there").</summary>
    private readonly HashSet<string> _endedFromDatabase = [];

    private async Task<Dictionary<string, JsonObject>> ReconcileAsync(Source source, Dictionary<string, JsonObject> known,
        Dictionary<string, JsonObject> active, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The cursor (the active set as seen now) is committed with the last message of this poll, so a crash before that
        // commit re-emits the same starts/ends on the next poll — and the identity (`{id}:start|end`) makes that a no-op.
        var cursor = new CollectorCheckpoint(null, active.Count == 0 ? null : now,
            JsonDocument.Parse(new JsonObject { ["active"] = new JsonArray(active.Values.Select(a => (JsonNode)a.DeepClone()).ToArray()) }.ToJsonString()));
        var messages = new List<IncomingMessage>();
        foreach (var (id, alert) in active)
        {
            if (known.ContainsKey(id))
            {
                continue;
            }
            var startedAt = ParseTime(alert["started_at"]) ?? now;
            messages.Add(Message(source, id, "start", "alert.started", alert, startedAt));
        }
        foreach (var (id, alert) in known)
        {
            if (!active.ContainsKey(id))
            {
                messages.Add(Message(source, id, "end", "alert.finished", alert, now));
            }
        }
        // Open in the database but neither active nor known: not ours to have started (dev/ingest), or a rebuild handled
        // the live end before it got to the start. The `{id}:end` message is idempotent, so a repeat is a no-op.
        foreach (var (id, alert) in await OpenInDatabaseAsync(source.SourceId, ct))
        {
            if (!active.ContainsKey(id) && !known.ContainsKey(id) && _endedFromDatabase.Add(id))
            {
                messages.Add(Message(source, id, "end", "alert.finished", alert, now));
                logger.LogInformation("alerts.in.ua: alert {Id} is open in the database but not in the feed; ended it", id);
            }
        }
        _endedFromDatabase.IntersectWith(await OpenIdsAsync(source.SourceId, ct)); // forget ids once the end is processed

        for (var i = 0; i < messages.Count; i++)
        {
            await ingress.PublishAsync(messages[i], source, Name, i == messages.Count - 1 ? cursor : null, live: true, ct);
        }
        if (messages.Count == 0)
        {
            await states.MarkSuccessAsync(source.SourceId, null, cursor.LastMessageAt, cursor.Cursor, ct);
        }
        return active;
    }

    private static IncomingMessage Message(Source source, string id, string phase, string kind, JsonObject alert, DateTimeOffset at) => new()
    {
        SourceId = source.SourceId,
        SourceMessageId = $"{id}:{phase}",
        SourceMessageKey = $"{id}:{phase}",
        SourceRevision = "0",
        PublishedAt = at,
        RawPayload = Wrap(kind, alert, at),
        Url = "https://alerts.in.ua/",
    };

    private async Task<HashSet<string>> OpenIdsAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AirAlerts.AsNoTracking().Where(a => a.SourceId == sourceId && a.EndedAt == null).Select(a => a.SourceAlertId).ToHashSetAsync(ct);
    }

    /// <summary>Alerts without an end in the database, with the alert object of their start message (the id alone when there is none).</summary>
    private async Task<List<(string Id, JsonObject Alert)>> OpenInDatabaseAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AirAlerts.AsNoTracking()
            .Where(a => a.SourceId == sourceId && a.EndedAt == null)
            .Select(a => new
            {
                a.SourceAlertId,
                a.StartedAt,
                Payload = db.RawMessages.Where(r => r.RawMessageId == a.StartRawMessageId).Select(r => r.RawPayload).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var result = new List<(string, JsonObject)>();
        foreach (var row in rows)
        {
            var alert = row.Payload is not null && row.Payload.RootElement.TryGetProperty("alert", out var el)
                        && JsonNode.Parse(el.GetRawText()) is JsonObject obj
                ? obj
                : new JsonObject { ["id"] = row.SourceAlertId, ["started_at"] = row.StartedAt.ToString("O") };
            result.Add((row.SourceAlertId, alert));
        }
        return result;
    }

    private async Task<Dictionary<string, JsonObject>> LoadKnownAsync(int sourceId, CancellationToken ct)
    {
        var state = await states.GetAsync(sourceId, ct);
        var result = new Dictionary<string, JsonObject>();
        if (state.Cursor is null || !state.Cursor.RootElement.TryGetProperty("active", out var arr))
        {
            return result;
        }
        foreach (var el in arr.EnumerateArray())
        {
            if (JsonNode.Parse(el.GetRawText()) is JsonObject alert && alert["id"] is not null)
            {
                result[alert["id"]!.ToString()] = alert;
            }
        }
        return result;
    }

    private static JsonDocument Wrap(string kind, JsonObject alert, DateTimeOffset at)
    {
        var payload = new JsonObject
        {
            ["kind"] = kind,
            ["at"] = at.ToString("O"),
            ["alert"] = alert.DeepClone(),
        };
        return JsonDocument.Parse(payload.ToJsonString());
    }

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        node is not null && DateTimeOffset.TryParse(node.ToString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
