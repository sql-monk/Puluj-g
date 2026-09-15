using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Settings;

namespace Puluj.Collectors.AlertsInUa;

/// <summary>
/// One-off load of the alerts.in.ua history (`/v1/regions/{uid}/alerts/{period}.json`, the deepest period the API
/// offers is `month_ago`) for every oblast, on top of the live collector that only sees the active feed. Each closed
/// alert becomes the same pair of RawMessages the live collector produces (`{id}:start` / `{id}:end`, payload kind
/// alert.started / alert.finished), so AlertsInUaHandler needs no second format and an alert already stored live is
/// skipped by the (source, source_message_id) uniqueness. The messages are stored Pending without a queue signal: the
/// processors take them in publication order. Progress (oblasts done) lives in app_settings
/// (`Runtime:AlertsInUa:History`), so a restart resumes and a completed period is not loaded twice; set a different
/// period, or clear the key, to load again. The history endpoint allows 2 calls a minute, hence the pacing.
/// </summary>
public sealed class AlertsInUaHistoryCollector(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<AlertsInUaOptions> options,
    CollectorIngress ingress,
    SettingsStore settings,
    TimeProvider clock,
    ILogger<AlertsInUaHistoryCollector> logger) : ICollector
{
    public const string StateKey = "AlertsInUa:History";
    public static readonly string[] Periods = ["month_ago", "week_ago"];

    /// <summary>Oblast uids of alerts.in.ua (https://devs.alerts.in.ua/): 24 oblasts, Crimea, Sevastopol, Kyiv.</summary>
    public static readonly int[] RegionUids = [3, 4, 5, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31];

    private static readonly TimeSpan CallSpacing = TimeSpan.FromSeconds(31);
    private static readonly TimeSpan RateLimitedWait = TimeSpan.FromSeconds(65);

    private sealed record HistoryState(
        [property: JsonPropertyName("period")] string Period,
        [property: JsonPropertyName("done")] List<int> Done,
        [property: JsonPropertyName("stored")] int Stored,
        [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt);

    public string Name => "alerts.in.ua history";

    public bool Handles(Source source) =>
        options.CurrentValue.Enabled
        && options.CurrentValue.BackfillPeriod is { Length: > 0 }
        && source.Type == SourceType.RestApi
        && source.Config?.RootElement.TryGetProperty("collector", out var c) == true
        && c.GetString() == AlertsInUaCollector.CollectorCode;

    public async Task RunAsync(IReadOnlyList<Source> sources, CancellationToken ct)
    {
        var source = sources[0];
        var period = options.CurrentValue.BackfillPeriod!.Trim().ToLowerInvariant();
        var token = AlertsInUaCollector.TokenFor(source, options.CurrentValue);
        if (!Periods.Contains(period) || string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("alerts.in.ua history: period '{Period}' unknown (use {Periods}) or token missing; idle", period, string.Join("/", Periods));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        var state = await ReadStateAsync(ct);
        if (state is null || state.Period != period)
        {
            state = new HistoryState(period, [], 0, clock.GetUtcNow(), null);
        }
        if (state.CompletedAt is not null)
        {
            logger.LogInformation("alerts.in.ua history: {Period} already loaded at {At} ({Stored} stored); idle", period, state.CompletedAt, state.Stored);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        var stored = state.Stored;
        foreach (var uid in RegionUids.Where(u => !state.Done.Contains(u)))
        {
            var alerts = await FetchAsync(token, uid, period, ct);
            var (added, skippedOpen) = await StoreAsync(source, alerts, ct);
            stored += added;
            state = state with { Done = [.. state.Done, uid], Stored = stored };
            await WriteStateAsync(state, ct);
            logger.LogInformation("alerts.in.ua history: region {Uid} ({Period}): {Count} alert(s), {Added} new message(s), {Open} still open; {Done}/{Total} regions",
                uid, period, alerts.Count, added, skippedOpen, state.Done.Count, RegionUids.Length);
            await Task.Delay(CallSpacing, ct);
        }

        state = state with { CompletedAt = clock.GetUtcNow() };
        await WriteStateAsync(state, ct);
        logger.LogInformation("alerts.in.ua history: {Period} complete, {Stored} raw message(s) stored; the processors take them in order", period, stored);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task<List<JsonObject>> FetchAsync(string token, int uid, string period, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(AlertsInUaCollector.HttpClientName);
        http.BaseAddress = new Uri(options.CurrentValue.BaseUrl);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var doc = await http.GetFromJsonAsync<JsonObject>($"v1/regions/{uid}/alerts/{period}.json", ct)
                          ?? throw new InvalidOperationException("Empty response");
                return doc["alerts"]?.AsArray().OfType<JsonObject>().Where(a => a["id"] is not null).ToList() ?? [];
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                logger.LogWarning("alerts.in.ua history: rate limited on region {Uid}; waiting {Wait}", uid, RateLimitedWait);
                await Task.Delay(RateLimitedWait, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 5)
            {
                logger.LogWarning(ex, "alerts.in.ua history: region {Uid} attempt {Attempt} failed", uid, attempt);
                await Task.Delay(CallSpacing, ct);
            }
        }
    }

    /// <summary>Start + end per closed alert; an alert without finished_at is still active and belongs to the live collector.</summary>
    private async Task<(int Added, int SkippedOpen)> StoreAsync(Source source, List<JsonObject> alerts, CancellationToken ct)
    {
        var added = 0;
        var open = 0;
        foreach (var alert in alerts.OrderBy(a => ParseTime(a["started_at"]) ?? DateTimeOffset.MinValue))
        {
            var id = alert["id"]!.ToString();
            var startedAt = ParseTime(alert["started_at"]);
            var finishedAt = ParseTime(alert["finished_at"]);
            if (startedAt is null)
            {
                continue;
            }
            if (finishedAt is null)
            {
                open++;
                continue;
            }
            if ((await Ingest(source, $"{id}:start", "alert.started", alert, startedAt.Value, ct)).Stored)
            {
                added++;
            }
            if ((await Ingest(source, $"{id}:end", "alert.finished", alert, finishedAt.Value, ct)).Stored)
            {
                added++;
            }
        }
        return (added, open);
    }

    private Task<IngressResult> Ingest(Source source, string messageId, string kind, JsonObject alert, DateTimeOffset at, CancellationToken ct) =>
        ingress.PublishAsync(new IncomingMessage
        {
            SourceId = source.SourceId,
            SourceMessageId = messageId,
            SourceMessageKey = messageId,
            SourceRevision = "0",
            PublishedAt = at,
            RawPayload = JsonDocument.Parse(new JsonObject
            {
                ["kind"] = kind,
                ["at"] = at.ToString("O"),
                ["history"] = true,
                ["alert"] = alert.DeepClone(),
            }.ToJsonString()),
            Url = "https://alerts.in.ua/",
        }, source, Name, checkpoint: null, live: false, ct);

    private async Task<HistoryState?> ReadStateAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync($"Runtime:{StateKey}", ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<HistoryState>(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "alerts.in.ua history: unreadable state, starting over");
            return null;
        }
    }

    private Task WriteStateAsync(HistoryState state, CancellationToken ct) =>
        settings.SetStatusAsync(StateKey, JsonSerializer.Serialize(state), ct);

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        node is not null && DateTimeOffset.TryParse(node.ToString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
