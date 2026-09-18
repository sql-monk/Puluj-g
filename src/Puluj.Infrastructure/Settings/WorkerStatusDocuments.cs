using System.Text.Json;
using System.Text.Json.Serialization;
using Puluj.Contracts;
using Puluj.Domain.Entities;

namespace Puluj.Infrastructure.Settings;

/// <summary>Parser for worker heartbeat and status documents stored in app_settings.</summary>
public static class WorkerStatusDocuments
{
    public static readonly TimeSpan Forgotten = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static List<(string Name, DateTimeOffset At)> Heartbeats(IReadOnlyDictionary<string, AppSetting> all, DateTimeOffset now)
    {
        var result = new List<(string, DateTimeOffset)>();
        foreach (var (key, setting) in all)
        {
            const string prefix = "Runtime:Worker:", suffix = ":Heartbeat";
            if (key.Length <= prefix.Length + suffix.Length
                || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
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

    public static WorkerStatusDto? Status(IReadOnlyDictionary<string, AppSetting> all, string name)
    {
        var key = $"Runtime:Worker:{name}:Status";
        var value = all.TryGetValue(key, out var exact) ? exact.Value
            : all.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkerStatusDto>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
