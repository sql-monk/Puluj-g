using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Puluj.Infrastructure.EntityExtraction;

public sealed class EntityDeliveryLoop(
    EntityDeliveryStore store,
    HttpClient http,
    IOptionsMonitor<EntityDeliveryOptions> options,
    EntityDeliveryIdentity identity,
    ILogger<EntityDeliveryLoop> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            var concurrency = Math.Clamp(current.Concurrency, 1, EntityDeliveryOptions.MaxConcurrency);
            var work = Enumerable.Range(0, concurrency).Select(_ => DeliverOneAsync(current, stoppingToken)).ToArray();
            var handled = await Task.WhenAll(work);
            if (!handled.Any(x => x))
            {
                await Task.Delay(current.PollingInterval > TimeSpan.Zero ? current.PollingInterval : TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<bool> DeliverOneAsync(EntityDeliveryOptions current, CancellationToken stoppingToken)
    {
        ClaimedEntityDelivery? claimed;
        try
        {
            claimed = await store.ClaimNextAsync(identity.Name, current.ClaimLease, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not claim an Entity Extractor delivery");
            return false;
        }
        if (claimed is null)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var outcome = "succeeded";
        int? statusCode = null;
        short? result = null;
        string? error = null;
        try
        {
            var endpoint = new Uri(new Uri(current.Url.TrimEnd('/') + "/"), "extract");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(claimed.Request, options: Json),
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", claimed.Request.DeliveryId.ToString());
            if (!string.IsNullOrWhiteSpace(current.Token))
            {
                request.Headers.TryAddWithoutValidation("X-Entity-Extractor-Token", current.Token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(current.DeliveryTimeout > TimeSpan.Zero ? current.DeliveryTimeout : TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                outcome = "http_error";
                error = $"HTTP {(int)response.StatusCode}: {Limit(body)}";
            }
            else if (!short.TryParse(body.Trim(), out var parsed) || parsed is not (0 or 1))
            {
                outcome = "invalid_response";
                error = $"Invalid Entity Extractor result: {Limit(body)}";
            }
            else
            {
                result = parsed;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            outcome = "timeout";
            error = "Entity Extractor request timed out.";
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            outcome = "connection_error";
            error = Limit(ex.Message);
        }

        try
        {
            await store.CompleteAsync(claimed, identity.Name, outcome, statusCode, result, error, stopwatch.Elapsed, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Leave the lease to expire. A later attempt uses the same delivery ID, so EE can return its cached result.
            logger.LogError(ex, "Could not record Entity Extractor delivery {DeliveryId}; the lease will recover it", claimed.Request.DeliveryId);
            return true;
        }

        if (outcome != "succeeded")
        {
            logger.LogWarning("Entity Extractor delivery {DeliveryId} failed ({Outcome}): {Error}; continuing with the queue",
                claimed.Request.DeliveryId, outcome, error);
        }
        return true;
    }

    private static string Limit(string value) => value.Length <= 4000 ? value : value[..4000];
}

public sealed record EntityDeliveryIdentity(string Name);
