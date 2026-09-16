using System.Text.Json;
using System.Text.Json.Nodes;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Processing.Indexes;

/// <summary>
/// Plan §8.2. Immutable snapshot of the event_kinds catalog: code → id and the legacy enum → id resolution the writer
/// stamps on every target. A processor takes one snapshot per message, so a catalog refresh never changes the kinds
/// in the middle of one message. Empty until the seeder has run; then targets get no kind and the backfill fills them.
/// </summary>
public sealed class EventKindIndex
{
    public static readonly EventKindIndex Empty = new([]);

    private readonly IReadOnlyDictionary<string, EventKind> _byCode;
    private readonly IReadOnlyDictionary<EventType, int> _byLegacy;

    public EventKindIndex(IReadOnlyCollection<EventKind> kinds)
    {
        _byCode = kinds.ToDictionary(k => k.Code, StringComparer.Ordinal);
        var byLegacy = new Dictionary<EventType, int>();
        foreach (var (type, code) in EventKindLegacyMap.All)
        {
            if (_byCode.TryGetValue(code, out var kind))
            {
                byLegacy[type] = kind.EventKindId;
            }
        }
        _byLegacy = byLegacy;
        // Highest policy version in the snapshot: what a target written with this snapshot can cite.
        PolicyVersion = kinds.Count == 0 ? 0 : kinds.Max(k => k.PolicyVersion);
    }

    public int Count => _byCode.Count;
    public bool IsEmpty => _byCode.Count == 0;
    public int PolicyVersion { get; }

    public EventKind? ByCode(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>Code of a catalog id (P10 incidents cite kinds by code), null when the snapshot does not know it.</summary>
    public string? CodeOf(int eventKindId) => _byCode.Values.FirstOrDefault(k => k.EventKindId == eventKindId)?.Code;

    /// <summary>Catalog id for a legacy enum value, or null when the catalog has not been seeded with that code (never a guess).</summary>
    public int? IdForLegacy(EventType type) => _byLegacy.TryGetValue(type, out var id) ? id : null;

    /// <summary>
    /// Stamps <see cref="Target.EventKindId"/> from the legacy enum on every target that has none yet; the enum column
    /// is kept (§8.2 compatibility). The catalog policy version the stamp came from goes into ParserMetadata
    /// (<c>eventKindPolicyVersion</c>) so the result can cite the snapshot it was written with.
    /// </summary>
    public int Stamp(IEnumerable<Target> targets)
    {
        var stamped = 0;
        foreach (var t in targets)
        {
            if (t.EventKindId is null && IdForLegacy(t.EventType) is int id)
            {
                t.EventKindId = id;
                t.ParserMetadata = WithPolicyVersion(t.ParserMetadata);
                stamped++;
            }
        }
        return stamped;
    }

    private JsonDocument WithPolicyVersion(JsonDocument? metadata)
    {
        var o = metadata is null ? new JsonObject() : JsonNode.Parse(metadata.RootElement.GetRawText())!.AsObject();
        o["eventKindPolicyVersion"] = PolicyVersion;
        return JsonDocument.Parse(o.ToJsonString());
    }
}
