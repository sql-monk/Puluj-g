using Json.Schema;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>Contract schemas loaded once per test run: JsonSchema.Net registers each `$id` globally and refuses a second registration.</summary>
public static class ContractSchemas
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "messaging");
    public static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

    private static readonly Lazy<(JsonSchema Envelope, JsonSchema RawStored, JsonSchema Ingress)> Loaded = new(() =>
    {
        JsonSchema.FromFile(Path.Combine(Root, "schemas", "common.schema.json"));
        return (
            JsonSchema.FromFile(Path.Combine(Root, "schemas", "envelope.schema.json")),
            JsonSchema.FromFile(Path.Combine(Root, "schemas", "events", "raw.stored.schema.json")),
            JsonSchema.FromFile(Path.Combine(Root, "schemas", "events", "ingress.received.schema.json")));
    });

    public static JsonSchema Envelope => Loaded.Value.Envelope;
    public static JsonSchema RawStored => Loaded.Value.RawStored;
    public static JsonSchema Ingress => Loaded.Value.Ingress;
}
