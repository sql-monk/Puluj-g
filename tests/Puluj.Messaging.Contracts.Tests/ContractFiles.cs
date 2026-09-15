using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Puluj.Messaging.Contracts.Tests;

/// <summary>
/// Доступ до contracts/messaging/ (скопійовано в output як Content) і зареєстровані схеми.
/// Один екземпляр на test run через collection fixture; сам по собі нічого не валідує.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ContractCollection : ICollectionFixture<ContractFiles>
{
    public const string Name = "contracts";
}

public sealed class ContractFiles
{
    public string Root { get; }
    public JsonObject Topology { get; }
    public JsonObject Manifest { get; }
    public JsonSchema Envelope { get; }
    public IReadOnlyDictionary<string, JsonSchema> PayloadSchemas { get; }
    public EvaluationOptions Options { get; }

    public ContractFiles()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "contracts", "messaging");
        Assert.True(Directory.Exists(Root), $"contracts directory not copied to output: {Root}");
        Topology = LoadObject("topology.json");
        Manifest = LoadObject("completion-manifest.json");

        Options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        // Схеми посилаються одна на одну через абсолютні $id; реєструємо всі, щоб $ref розв'язувалися без мережі.
        // FromFile реєструє схему в SchemaRegistry.Global за її $id; повторна реєстрація заборонена, тому один
        // екземпляр на весь test run (collection fixture).
        JsonSchema.FromFile(Path.Combine(Root, "schemas", "common.schema.json"));
        Envelope = JsonSchema.FromFile(Path.Combine(Root, "schemas", "envelope.schema.json"));

        var payloads = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var (eventType, definition) in Events)
        {
            var relative = definition!["schema"]!.GetValue<string>();
            payloads[eventType] = JsonSchema.FromFile(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        PayloadSchemas = payloads;
    }

    public IEnumerable<KeyValuePair<string, JsonNode?>> Events => Topology["events"]!.AsObject();
    public IEnumerable<KeyValuePair<string, JsonNode?>> Subscriptions => Topology["subscriptions"]!.AsObject();
    public IEnumerable<KeyValuePair<string, JsonNode?>> ProducerRoles => Topology["producer_roles"]!.AsObject();

    public JsonObject LoadObject(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relative)))!.AsObject();

    public IEnumerable<string> Files(string relativeDirectory, string pattern = "*.json") =>
        Directory.EnumerateFiles(Path.Combine(Root, relativeDirectory), pattern).OrderBy(p => p, StringComparer.Ordinal);

    public static IEnumerable<string> Strings(JsonNode? array) =>
        array is null ? [] : array.AsArray().Select(n => n!.GetValue<string>());

    /// <summary>Envelope + payload схема свого event_type (payload_ref → лише envelope).</summary>
    public (bool IsValid, string Errors) Validate(JsonNode envelope)
    {
        var result = Envelope.Evaluate(ToElement(envelope), Options);
        if (!result.IsValid)
        {
            return (false, Describe(result));
        }
        var eventType = envelope["event_type"]!.GetValue<string>();
        if (envelope["payload"] is { } payload)
        {
            var payloadResult = PayloadSchemas[eventType].Evaluate(ToElement(payload), Options);
            if (!payloadResult.IsValid)
            {
                return (false, Describe(payloadResult, "/payload"));
            }
        }
        return (true, "");
    }

    public static string Describe(EvaluationResults results, string prefix = "")
    {
        var lines = (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{prefix}{d.InstanceLocation}: {e.Key}: {e.Value}"))
            .Distinct();
        return string.Join("; ", lines);
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    public static string Pretty(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
