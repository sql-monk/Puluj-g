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

    [Fact]
    public void U05_Public_message_contract_keeps_bigint_and_text_allowlist_separate_from_processing_metadata()
    {
        var id = "9007199254740993";
        var summary = new PublicMessageSummaryDto(id, 7, "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "post-1", "e42", "7:post-1",
            "completed", "stage_result", "https://example.test/post", 1, 1, 1, 0, true);
        var map = new PublicMapLocatorDto("point", null, null, null, "point", null, DateTimeOffset.UnixEpoch, null);
        var result = new PublicMessageResultDto(id, null, 0, "allowed segment", "event", null, DateTimeOffset.UnixEpoch, "high", 7, true, map, []);
        var detail = new PublicMessageDetailsDto(summary, "available", "allowed full text", new([result], null, 1), new([], null, 0), [], new Dictionary<string, string>());
        var options = new JsonSerializerOptions();
        ApiDependencyInjection.ConfigureJson(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, options));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("message").GetProperty("id").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("rawPayload", out _));
        Assert.False(json.RootElement.TryGetProperty("parserMetadata", out _));
        Assert.False(json.RootElement.TryGetProperty("worker", out _));
    }
}
