using System.Text.Json;
using Puluj.Api;
using Puluj.Contracts;

namespace Puluj.Api.Tests;

/// <summary>U04 wire guard: catalogue identities deliberately do not use JS numbers, and public message references stay an allow-list.</summary>
public class PublicCatalogContractTests
{
    [Fact]
    public void U04_Catalogue_bigint_ids_are_strings_and_public_messages_do_not_leak_text()
    {
        var idBeyondJavaScriptSafeInteger = "9007199254740993";
        var map = new PublicMapLocatorDto(null, null, null, null, null, null, null, "no_reported_location");
        var row = new PublicEntitySummaryDto("observation", idBeyondJavaScriptSafeInteger, "Факт", null, null, null,
            DateTimeOffset.UnixEpoch, null, null, null, null, null, null, [], 1, 1, false, map);
        var detail = new PublicEntityDetailsDto(row,
            new PublicCollectionPageDto<PublicEvidenceDto>([new("observation", idBeyondJavaScriptSafeInteger, null, idBeyondJavaScriptSafeInteger, 7, DateTimeOffset.UnixEpoch, null, null, null, null, "self", null)], null, 1),
            new PublicCollectionPageDto<PublicMessageRefDto>([new(idBeyondJavaScriptSafeInteger, 7, DateTimeOffset.UnixEpoch, "https://example.test/post")], null, 1),
            new PublicCollectionPageDto<PublicEntityRefDto>([], null, 0), new Dictionary<string, string>(), new Dictionary<string, bool>());

        var options = new JsonSerializerOptions();
        ApiDependencyInjection.ConfigureJson(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, options));
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("entity").GetProperty("id").ValueKind);
        Assert.Equal(idBeyondJavaScriptSafeInteger, root.GetProperty("entity").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("messages").GetProperty("items")[0].GetProperty("id").ValueKind);
        Assert.False(root.GetProperty("messages").GetProperty("items")[0].TryGetProperty("text", out _));
        Assert.False(root.GetProperty("messages").GetProperty("items")[0].TryGetProperty("rawPayload", out _));
    }
}
