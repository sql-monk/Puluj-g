using System.Security.Cryptography;
using System.Text;

namespace Puluj.Infrastructure.Messaging;

/// <summary>
/// Raw identity of ADR-0003 derived from the legacy `raw_messages.source_message_id` (rules of
/// `contracts/messaging/fixtures/identity-cases.json`): a Telegram edit `"{id}:e{unix}"` is revision `e{unix}` of key `{id}`;
/// everything else (originals, alerts `"{id}:start|end"`, unknown sources) is revision `"0"` of the id itself.
/// The proper split at the collectors is P04; until then the bridge maps here so the archive carries the final identity.
/// </summary>
public readonly record struct SourceIdentity(string SourceMessageKey, string SourceRevision)
{
    /// <summary>Namespace for the deterministic correlation id of a source post (UUIDv5 over `{source_id}:{key}`).</summary>
    private static readonly Guid CorrelationNamespace = new("6c1c7d6e-2f7b-4bb4-9a8f-0f6e7f5a2d11");

    public static SourceIdentity FromLegacy(string sourceMessageId)
    {
        var colon = sourceMessageId.LastIndexOf(":e", StringComparison.Ordinal);
        if (colon > 0 && colon + 2 < sourceMessageId.Length && sourceMessageId.AsSpan(colon + 2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return new SourceIdentity(sourceMessageId[..colon], sourceMessageId[(colon + 1)..]);
        }
        return new SourceIdentity(sourceMessageId, "0");
    }

    /// <summary>
    /// One correlation id per source post: the original and each of its revisions share it (ADR-0003), and a republish
    /// or a second collector of the same post computes the same value without a lookup.
    /// </summary>
    public static Guid CorrelationId(int sourceId, string sourceMessageKey) => NameBasedGuid(CorrelationNamespace, $"{sourceId}:{sourceMessageKey}");

    /// <summary>RFC 4122 version 5 (SHA-1 name-based) UUID.</summary>
    public static Guid NameBasedGuid(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var material = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(material, 0);
        nameBytes.CopyTo(material, nsBytes.Length);
        var hash = SHA1.HashData(material);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }
}
