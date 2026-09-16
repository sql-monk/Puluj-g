using System.Text.Json;
using System.Text.Json.Serialization;
using Puluj.Contracts;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// The worker telemetry in `app_settings` (docs/plan-admin-ops.md §2.1): `Runtime:Worker:{name}:Heartbeat` every 30 s
/// and `Runtime:Worker:{name}:Status` (a <see cref="WorkerStatusDto"/> JSON) every 10 s. Shared by the admin panel's
/// workers view and the P13 ops snapshot so both apply the same parsing and the same forgetting window.
/// </summary>
public static class WorkerStatusDocuments
{
    /// <summary>A killed dev process leaves its key behind: it is shown as down for a while and then forgotten.</summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromMinutes(15);

    /// <summary>The Worker writes its status document in camelCase (the web JSON options); enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>Heartbeats of the Worker instances, oldest first; the pre-roles key `Runtime:Worker:Heartbeat` (no instance name) is skipped.</summary>
    public static List<(string Name, DateTimeOffset At)> Heartbeats(IReadOnlyDictionary<string, AppSetting> all, DateTimeOffset now)
    {
        var result = new List<(string, DateTimeOffset)>();
        foreach (var (key, setting) in all)
        {
            const string prefix = "Runtime:Worker:", suffix = ":Heartbeat";
            if (key.Length <= prefix.Length + suffix.Length
                || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var name = key[prefix.Length..^suffix.Length];
            if (DateTimeOffset.TryParse(setting.Value, out var at) && now - at < Forgotten)
            {
                result.Add((name, at));
            }
        }
        return result.OrderBy(w => w.Item2).ToList();
    }

    /// <summary>The instance's own status document; null when absent or unreadable.</summary>
    public static WorkerStatusDto? Status(IReadOnlyDictionary<string, AppSetting> all, string name)
    {
        var key = $"Runtime:Worker:{name}:Status";
        var value = all.TryGetValue(key, out var exact) ? exact.Value
            : all.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WorkerStatusDto>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The last reconciliation report of the messaging worker (`Runtime:Reconciliation:Report`); null when none was written yet.</summary>
    public static ReconciliationReportDto? Reconciliation(IReadOnlyDictionary<string, AppSetting> all)
    {
        if (!all.TryGetValue("Runtime:Reconciliation:Report", out var setting) || string.IsNullOrWhiteSpace(setting.Value))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ReconciliationReportDto>(setting.Value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
