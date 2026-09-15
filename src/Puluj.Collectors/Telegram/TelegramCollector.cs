using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Settings;
using TL;

namespace Puluj.Collectors.Telegram;

/// <summary>
/// One MTProto session (WTelegramClient) serving every enabled Telegram source. Backfills recent history on start
/// (or, with BackfillSince, the whole history from that instant, once), then stores every new/edited channel post as
/// a RawMessage. Edits become separate RawMessages (key = message id, revision "e{editDate}"; legacy id "{id}:e{editDate}")
/// so the original text is never lost (spec §5: RawMessage is immutable). Messages go through <see cref="CollectorIngress"/>:
/// with the ingress enabled the checkpoint (last message id / history cursor) is committed with the message (plan §6.1).
/// </summary>
public sealed class TelegramCollector(
    IOptionsMonitor<TelegramOptions> options,
    CollectorIngress ingress,
    ReprocessService reprocess,
    CollectorStateStore states,
    SettingsStore settings,
    ILogger<TelegramCollector> logger) : ICollector
{
    public const string StatusKey = "Telegram:Status";

    public string Name => "telegram";

    private readonly Dictionary<long, (Source Source, string Username)> _channels = [];
    private TelegramOptions _o = new();
    /// <summary>True while the whole-history load runs: live posts are stored without being queued, like the history.</summary>
    private volatile bool _loadingHistory;

    /// <summary>Progress of the whole-history load of one channel, kept in collector_states.cursor.</summary>
    private sealed record HistoryCursor(
        [property: JsonPropertyName("since")] DateTimeOffset Since,
        [property: JsonPropertyName("lastId")] int LastId,
        [property: JsonPropertyName("stored")] long Stored,
        [property: JsonPropertyName("done")] bool Done);

    public bool Handles(Source source) => options.CurrentValue.Enabled && source.Type == SourceType.Telegram && Username(source) is not null;

    public async Task RunAsync(IReadOnlyList<Source> sources, CancellationToken ct)
    {
        _o = options.CurrentValue;
        var o = _o;
        _channels.Clear();
        if (o.ApiId == 0 || string.IsNullOrWhiteSpace(o.ApiHash) || string.IsNullOrWhiteSpace(o.Phone))
        {
            logger.LogWarning("Telegram ApiId/ApiHash/Phone missing (Collectors:Telegram:*); collector idle");
            await settings.SetStatusAsync(StatusKey, "missing_credentials", ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }
        // WTelegramClient opens the session file *before* validating api_hash; a bad hash would throw with the file
        // handle leaked, so validate here and stay idle instead of crash-looping.
        if (!IsValidApiHash(o.ApiHash))
        {
            logger.LogWarning("Telegram api_hash must be 32 hex characters; collector idle");
            await settings.SetStatusAsync(StatusKey, "error: api_hash має бути 32 hex-символи (перевірте значення з my.telegram.org)", ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.SessionPath))!);
        WTelegram.Helpers.Log = (level, msg) => logger.Log(level switch
        {
            >= 4 => LogLevel.Error,
            3 => LogLevel.Warning,
            2 => LogLevel.Information,
            _ => LogLevel.Debug,
        }, "WTelegram: {Message}", msg);

        try
        {
            await RunSessionAsync(sources, o, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await settings.SetStatusAsync(StatusKey, "error: " + ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task RunSessionAsync(IReadOnlyList<Source> sources, TelegramOptions o, CancellationToken ct)
    {
        using var client = new WTelegram.Client(Config);
        var manager = client.WithUpdateManager(OnUpdate, o.SessionPath + ".updates");
        await settings.SetStatusAsync(StatusKey, "connecting", ct);
        TL.User user;
        try
        {
            user = await client.LoginUserIfNeeded();
        }
        catch (Exception ex)
        {
            await settings.SetStatusAsync(StatusKey, "error: " + ex.Message, CancellationToken.None);
            throw;
        }
        logger.LogInformation("Telegram: logged in as {User} (id {Id})", user.username ?? user.first_name, user.id);
        await settings.SetStatusAsync(StatusKey, $"logged_in: {user.first_name} {user.last_name} (@{user.username})".Trim(), ct);

        // Let the update manager know about our dialogs so peers resolve; then resolve each configured channel.
        await manager.LoadDialogs(await client.Messages_GetAllDialogs());
        foreach (var source in sources)
        {
            var username = Username(source)!;
            try
            {
                var resolved = await client.Contacts_ResolveUsername(username);
                if (resolved.Chat is not Channel channel)
                {
                    logger.LogWarning("Telegram: @{Username} is not a channel; skipping {Source}", username, source.Code);
                    continue;
                }
                if (o.AutoJoin && channel.flags.HasFlag(Channel.Flags.left))
                {
                    await client.Channels_JoinChannel(channel);
                    logger.LogInformation("Telegram: joined @{Username}", username);
                }
                _channels[channel.id] = (source, username);
                await BackfillAsync(client, channel, source, username, ct);
            }
            catch (RpcException ex)
            {
                logger.LogWarning(ex, "Telegram: cannot set up @{Username} ({Source})", username, source.Code);
                await states.MarkFailureAsync(source.SourceId, ex.Message, ct);
            }
        }
        if (o.BackfillSince is { } since)
        {
            await LoadHistoryAsync(client, since, ct);
        }
        logger.LogInformation("Telegram: listening to {Count} channel(s)", _channels.Count);
        await settings.SetStatusAsync(StatusKey, $"listening: {_channels.Count} channel(s) as @{user.username ?? user.first_name}", ct);

        // Keep the session alive until cancelled; WTelegramClient reconnects on its own.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            manager.SaveState(o.SessionPath + ".updates");
            await settings.SetStatusAsync(StatusKey, "stopped", CancellationToken.None);
        }
    }

    private string? Config(string what)
    {
        var o = _o;
        return what switch
        {
            "api_id" => o.ApiId.ToString(),
            "api_hash" => o.ApiHash,
            "phone_number" => o.Phone,
            "password" => o.Password,
            "session_pathname" => o.SessionPath,
            "verification_code" => WaitForVerificationCode(),
            _ => null,
        };
    }

    /// <summary>
    /// First login only. The code can arrive three ways: the admin UI (app_settings key Collectors:Telegram:VerificationCode,
    /// consumed and cleared), the configuration value, or a file &lt;SessionPath&gt;.code. Waits up to 10 minutes.
    /// </summary>
    private string? WaitForVerificationCode()
    {
        var o = _o;
        const string key = "Collectors:Telegram:VerificationCode";
        var codeFile = o.SessionPath + ".code";
        logger.LogWarning("Telegram asks for the login code sent to {Phone}. Enter it in the admin UI, set {Key}, or write it into {File}. Waiting up to 10 minutes.", o.Phone, key, codeFile);
        settings.SetStatusAsync(StatusKey, "waiting_code", CancellationToken.None).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            var fromDb = settings.GetAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(fromDb))
            {
                settings.SetAsync(new Dictionary<string, string?> { [key] = null }, CancellationToken.None).GetAwaiter().GetResult();
                return fromDb.Trim();
            }
            if (!string.IsNullOrWhiteSpace(o.VerificationCode))
            {
                return o.VerificationCode;
            }
            if (File.Exists(codeFile))
            {
                var code = File.ReadAllText(codeFile).Trim();
                File.Delete(codeFile);
                if (code.Length > 0)
                {
                    return code;
                }
            }
            Thread.Sleep(2000);
        }
        throw new TimeoutException("Telegram verification code was not provided in time.");
    }

    private async Task BackfillAsync(WTelegram.Client client, Channel channel, Source source, string username, CancellationToken ct)
    {
        var state = await states.GetAsync(source.SourceId, ct);
        var minId = int.TryParse(state.LastSourceMessageId?.Split(':')[0], out var last) ? last : 0;
        var history = await client.Messages_GetHistory(channel, limit: Math.Clamp(_o.BackfillLimit, 1, 100), min_id: minId);
        var messages = history.Messages.OfType<Message>().OrderBy(m => m.id).ToList();
        var stored = 0;
        foreach (var m in messages)
        {
            // The checkpoint travels with the message (ids are monotonic within a channel): a crash between them cannot
            // move the cursor past an unpublished post.
            if (await StoreAsync(m, source, username, ct, checkpoint: new CollectorCheckpoint(m.id.ToString(), ToUtc(m.date))))
            {
                stored++;
            }
        }
        var newest = messages.LastOrDefault();
        await states.MarkSuccessAsync(source.SourceId, newest?.id.ToString(), newest is null ? null : ToUtc(newest.date), null, ct);
        logger.LogInformation("Telegram: @{Username} backfill {Stored}/{Total} new (min_id {MinId})", username, stored, messages.Count, minId);
    }

    /// <summary>
    /// The whole history of every channel from `since` on, oldest first, page by page (offset_id + add_offset = -limit
    /// is the forward-paging form of messages.getHistory). Resumable: the cursor (last id stored) lives in
    /// collector_states, so a restart continues where it stopped. Processing is held for the duration and every
    /// message is stored Pending only; when the last channel is done, everything derived is rebuilt from the raw
    /// messages in publication order — the channels are read one after another, so processing them as they arrive
    /// would put a channel's 2022 after another's 2026.
    /// </summary>
    private async Task LoadHistoryAsync(WTelegram.Client client, DateTimeOffset since, CancellationToken ct)
    {
        var pending = new List<(Channel Channel, Source Source, string Username, HistoryCursor Cursor)>();
        foreach (var (channelId, (source, username)) in _channels)
        {
            var state = await states.GetAsync(source.SourceId, ct);
            var cursor = ReadCursor(state.Cursor);
            if (cursor is { Done: true } && cursor.Since <= since)
            {
                continue;
            }
            var channel = await ResolveAsync(client, username);
            if (channel is null || channel.id != channelId)
            {
                continue;
            }
            pending.Add((channel, source, username, cursor is null || cursor.Since > since ? new HistoryCursor(since, 0, 0, false) : cursor));
        }
        if (pending.Count == 0)
        {
            return;
        }
        _loadingHistory = true;
        await reprocess.PauseAsync($"history load: {pending.Count} channel(s) since {since:yyyy-MM-dd}", ct);
        try
        {
            foreach (var (channel, source, username, start) in pending)
            {
                await settings.SetStatusAsync(StatusKey, $"history: @{username} since {since:yyyy-MM-dd}", ct);
                await LoadChannelHistoryAsync(client, channel, source, username, start, ct);
            }
            // Everything is in: rebuild in order. Held until now so no message was processed out of sequence. Through the
            // ingress the raw rows are written by the raw-writer: wait until every published message of these channels
            // has one, otherwise the rebuild would start before the history is complete.
            await settings.SetStatusAsync(StatusKey, "history: waiting for the raw-writer", ct);
            if (!await ingress.WaitForDrainAsync(pending.Select(p => p.Source.SourceId).ToList(), ct))
            {
                await settings.SetStatusAsync(StatusKey, "history: drain timeout, rebuilding anyway", ct);
            }
            await settings.SetStatusAsync(StatusKey, "history: rebuilding derived data", ct);
            var queued = await reprocess.ResetAsync(ct);
            logger.LogInformation("Telegram: history load complete, {Count} raw message(s) queued for processing in order", queued);
        }
        finally
        {
            _loadingHistory = false;
            await reprocess.ResumeAsync(CancellationToken.None);
        }
    }

    private async Task LoadChannelHistoryAsync(WTelegram.Client client, Channel channel, Source source, string username, HistoryCursor cursor, CancellationToken ct)
    {
        const int limit = 100;
        var lastId = cursor.LastId;
        var stored = cursor.Stored;
        var pages = 0;
        while (!ct.IsCancellationRequested)
        {
            Messages_MessagesBase history;
            try
            {
                // The `limit` messages right after the last id seen; the first page starts after id 1 (the oldest the
                // account can see). An offset_date start is not used: on one channel it returned the newest page.
                history = await client.Messages_GetHistory(channel, offset_id: Math.Max(1, lastId), add_offset: -limit, limit: limit);
            }
            catch (RpcException ex) when (ex.Code == 420)
            {
                // FLOOD_WAIT longer than the client's own retry threshold: wait it out and go on.
                logger.LogWarning("Telegram: @{Username} flood wait {Seconds}s", username, ex.X);
                await Task.Delay(TimeSpan.FromSeconds(ex.X + 1), ct);
                continue;
            }
            var page = history.Messages.OfType<Message>().Where(m => m.id > lastId).OrderBy(m => m.id).ToList();
            if (page.Count == 0)
            {
                break;
            }
            // Pages before `since` are only paged past, not stored. The page cursor is committed with the last stored
            // message of the page (or on its own when the page stored nothing), so a restart resumes from a page whose
            // messages are all published.
            var toStore = page.Where(m => ToUtc(m.date) >= cursor.Since).ToList();
            var messages = page;
            lastId = messages[^1].id;
            pages++;
            var pageCursor = WriteCursor(new HistoryCursor(cursor.Since, lastId, stored + toStore.Count, false));
            for (var i = 0; i < toStore.Count; i++)
            {
                var last = i == toStore.Count - 1;
                if (await StoreAsync(toStore[i], source, username, ct, enqueue: false, checkpoint: last ? new CollectorCheckpoint(null, ToUtc(messages[^1].date), pageCursor) : null))
                {
                    stored++;
                }
            }
            if (toStore.Count == 0)
            {
                await states.MarkSuccessAsync(source.SourceId, null, ToUtc(messages[^1].date), pageCursor, ct);
            }
            if (pages % 20 == 0)
            {
                logger.LogInformation("Telegram: @{Username} history … id {LastId} ({Date:yyyy-MM-dd HH:mm}), {Stored} stored", username, lastId, ToUtc(messages[^1].date), stored);
            }
            // Telegram tolerates a steady trickle far better than bursts.
            await Task.Delay(300, ct);
        }
        await states.MarkSuccessAsync(source.SourceId, lastId == 0 ? null : lastId.ToString(), null, WriteCursor(new HistoryCursor(cursor.Since, lastId, stored, true)), ct);
        logger.LogInformation("Telegram: @{Username} history done since {Since:yyyy-MM-dd}: {Stored} stored, last id {LastId}", username, cursor.Since, stored, lastId);
    }

    private async Task<Channel?> ResolveAsync(WTelegram.Client client, string username)
    {
        try
        {
            return (await client.Contacts_ResolveUsername(username)).Chat as Channel;
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "Telegram: cannot resolve @{Username} for the history load", username);
            return null;
        }
    }

    private static HistoryCursor? ReadCursor(JsonDocument? cursor)
    {
        if (cursor is null || !cursor.RootElement.TryGetProperty("history", out var h))
        {
            return null;
        }
        try
        {
            return h.Deserialize<HistoryCursor>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument WriteCursor(HistoryCursor cursor) => JsonSerializer.SerializeToDocument(new { history = cursor });

    private async Task OnUpdate(Update update)
    {
        try
        {
            switch (update)
            {
                case UpdateNewChannelMessage { message: Message m }:
                    await HandleAsync(m, isEdit: false);
                    break;
                case UpdateEditChannelMessage { message: Message m }:
                    await HandleAsync(m, isEdit: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram: update handling failed");
        }
    }

    private async Task HandleAsync(Message m, bool isEdit)
    {
        if (m.peer_id is not PeerChannel pc || !_channels.TryGetValue(pc.channel_id, out var entry))
        {
            return;
        }
        var (source, username) = entry;
        // An edit does not move the id checkpoint (it belongs to an old post); the date still counts as activity.
        await StoreAsync(m, source, username, CancellationToken.None, enqueue: !_loadingHistory,
            checkpoint: new CollectorCheckpoint(isEdit ? null : m.id.ToString(), ToUtc(m.date)));
    }

    /// <summary>Publishes one post (or edit); returns whether it was stored/accepted (false for service messages and known duplicates).</summary>
    private async Task<bool> StoreAsync(Message m, Source source, string username, CancellationToken ct, bool enqueue = true, CollectorCheckpoint? checkpoint = null)
    {
        if (string.IsNullOrWhiteSpace(m.message) && m.media is null)
        {
            if (checkpoint is not null)
            {
                await states.MarkSuccessAsync(source.SourceId, checkpoint.LastSourceMessageId, checkpoint.LastMessageAt, checkpoint.Cursor, ct);
            }
            return false; // service messages still move the cursor
        }
        var payload = TelegramMessagePayload.From(m, username);
        var revision = payload.EditDate is null ? "0" : $"e{payload.EditDate.Value.ToUnixTimeSeconds()}";
        var result = await ingress.PublishAsync(new IncomingMessage
        {
            SourceId = source.SourceId,
            SourceMessageId = revision == "0" ? m.id.ToString() : $"{m.id}:{revision}",
            SourceMessageKey = m.id.ToString(),
            SourceRevision = revision,
            PublishedAt = payload.EditDate ?? payload.Date,
            RawText = m.message,
            RawPayload = payload.ToDocument(),
            Url = $"https://t.me/{username}/{m.id}",
        }, source, Name, checkpoint, live: enqueue, ct);
        return result.Stored;
    }

    private static string? Username(Source source)
    {
        if (source.Config is null || !source.Config.RootElement.TryGetProperty("channel", out var c))
        {
            return null;
        }
        var s = NormalizeUsername(c.GetString());
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Accepts "@name", "name", "https://t.me/name" or "t.me/name/123" and returns "name".</summary>
    public static string? NormalizeUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        var idx = s.IndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            s = s[(idx + 5)..];
        }
        s = s.TrimStart('@').Split('/', '?', '#')[0].Trim();
        return s.Length == 0 ? null : s;
    }

    public static bool IsValidApiHash(string? hash) => hash is { Length: 32 } && hash.All(Uri.IsHexDigit);

    private static DateTimeOffset ToUtc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
