using System.Text.Json;
using System.Text.Json.Serialization;
using TL;

namespace Puluj.Collectors.Telegram;

/// <summary>Lossless-enough snapshot of a Telegram message stored in RawMessage.RawPayload.</summary>
public sealed record TelegramMessagePayload
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "telegram.message";
    [JsonPropertyName("channelId")] public long ChannelId { get; init; }
    [JsonPropertyName("channel")] public string? Channel { get; init; }
    [JsonPropertyName("channelTitle")] public string? ChannelTitle { get; init; }
    [JsonPropertyName("messageId")] public int MessageId { get; init; }
    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; }
    [JsonPropertyName("editDate")] public DateTimeOffset? EditDate { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("forwardedFrom")] public string? ForwardedFrom { get; init; }
    [JsonPropertyName("forwardedDate")] public DateTimeOffset? ForwardedDate { get; init; }
    [JsonPropertyName("hasMedia")] public bool HasMedia { get; init; }
    [JsonPropertyName("mediaType")] public string? MediaType { get; init; }
    [JsonPropertyName("views")] public int? Views { get; init; }
    /// <summary>Channel audience visible to Telegram when this post was collected; null means Telegram did not disclose it.</summary>
    [JsonPropertyName("subscriberCount")] public int? SubscriberCount { get; init; }
    /// <summary>Per-reaction counters supplied with the post. No reacting-user identities are retained.</summary>
    [JsonPropertyName("reactions")] public List<TelegramReactionPayload> Reactions { get; init; } = [];
    /// <summary>Total comments shown by Telegram for this post; null means the post has no disclosed comment section.</summary>
    [JsonPropertyName("commentCount")] public int? CommentCount { get; init; }
    /// <summary>Discussion group ID, preserved as a decimal string so it is safe for JavaScript consumers.</summary>
    [JsonPropertyName("discussionChannelId")] public string? DiscussionChannelId { get; init; }
    [JsonPropertyName("groupedId")] public long? GroupedId { get; init; }
    [JsonPropertyName("urls")] public List<string> Urls { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TelegramMessagePayload From(Message m, string? channelUsername, int? subscriberCount = null, string? channelTitle = null)
    {
        var urls = new List<string>();
        foreach (var e in m.entities ?? [])
        {
            if (e is MessageEntityTextUrl tu)
            {
                urls.Add(tu.url);
            }
            else if (e is MessageEntityUrl u && m.message is not null && u.offset + u.length <= m.message.Length)
            {
                urls.Add(m.message.Substring(u.offset, u.length));
            }
        }
        var fwd = m.fwd_from;
        var replies = m.replies;
        return new TelegramMessagePayload
        {
            ChannelId = m.peer_id is PeerChannel pc ? pc.channel_id : 0,
            Channel = channelUsername,
            ChannelTitle = channelTitle,
            MessageId = m.id,
            Date = new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc)),
            EditDate = m.edit_date == default ? null : new DateTimeOffset(DateTime.SpecifyKind(m.edit_date, DateTimeKind.Utc)),
            Text = m.message,
            ForwardedFrom = fwd?.from_name ?? fwd?.from_id?.ToString(),
            ForwardedDate = fwd is null || fwd.date == default ? null : new DateTimeOffset(DateTime.SpecifyKind(fwd.date, DateTimeKind.Utc)),
            HasMedia = m.media is not null,
            MediaType = m.media?.GetType().Name,
            Views = m.views == 0 ? null : m.views,
            SubscriberCount = subscriberCount,
            Reactions = (m.reactions?.results ?? []).Select(ToReactionPayload).ToList(),
            CommentCount = replies?.replies,
            DiscussionChannelId = replies?.channel_id == 0 ? null : replies?.channel_id.ToString(),
            GroupedId = m.grouped_id == 0 ? null : m.grouped_id,
            Urls = urls,
        };
    }

    private static TelegramReactionPayload ToReactionPayload(ReactionCount reaction) => new()
    {
        Kind = reaction.reaction switch
        {
            ReactionEmoji emoji => "emoji",
            ReactionCustomEmoji => "custom_emoji",
            ReactionPaid => "paid",
            _ => "unknown",
        },
        Value = reaction.reaction switch
        {
            ReactionEmoji emoji => emoji.emoticon,
            ReactionCustomEmoji custom => custom.document_id.ToString(),
            ReactionPaid => "⭐",
            _ => reaction.reaction?.GetType().Name,
        },
        Count = reaction.count,
    };

    public JsonDocument ToDocument() => JsonDocument.Parse(JsonSerializer.Serialize(this, Json));
}

/// <summary>A public aggregate from Telegram, deliberately excluding the identities of people who reacted.</summary>
public sealed record TelegramReactionPayload
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
}
