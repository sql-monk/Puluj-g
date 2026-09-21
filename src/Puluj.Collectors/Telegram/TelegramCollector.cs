using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
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
    TimeProvider clock,
    ILogger<TelegramCollector> logger) : ICollector
{
    public const string StatusKey = "Telegram:Status";

    public string Name => "telegram";

    private readonly Dictionary<long, (Source Source, string Username, string? Title, int? SubscriberCount)> _channels = [];
    private TelegramOptions _o = new();
    private TelegramRequestGate? _requestGate;
    private TelegramRpcExecutor? _rpc;
    /// <summary>True while the whole-history load runs: live posts are stored without waking the processor, like the history.</summary>
    private volatile bool _loadingHistory;

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
        // The scheduler owns flood handling. Letting WTelegram sleep/retry invisibly would bypass the global gate.
        client.FloodRetryThreshold = 0;
        _requestGate = new TelegramRequestGate(clock, o.HistoryRequestInterval, o.HistoryMinimumInterval, o.HistoryMaximumInterval);
        _rpc = new TelegramRpcExecutor(_requestGate, o.RpcTimeout);
        var manager = client.WithUpdateManager(OnUpdate, o.SessionPath + ".updates");
        await settings.SetStatusAsync(StatusKey, "connecting", ct);
        TL.User user;
        try
        {
            user = await GetTelegramRpcAsync(() => client.LoginUserIfNeeded(), "login", ct);
        }
        catch (Exception ex)
        {
            await settings.SetStatusAsync(StatusKey, "error: " + ex.Message, CancellationToken.None);
            throw;
        }
        logger.LogInformation("Telegram: logged in as {User} (id {Id})", user.username ?? user.first_name, user.id);
        await settings.SetStatusAsync(StatusKey, $"logged_in: {user.first_name} {user.last_name} (@{user.username})".Trim(), ct);

        // Let the update manager know about our dialogs so peers resolve; then resolve each configured channel.
        await manager.LoadDialogs(await GetTelegramRpcAsync(() => client.Messages_GetAllDialogs(), "load dialogs", ct));
        var resolvedChannels = new List<(Channel Channel, Source Source, string Username)>();
        foreach (var source in sources)
        {
            var username = Username(source)!;
            try
            {
                var resolved = await GetTelegramRpcAsync(() => client.Contacts_ResolveUsername(username), $"resolve @{username}", ct);
                if (resolved.Chat is not Channel channel)
                {
                    logger.LogWarning("Telegram: @{Username} is not a channel; skipping {Source}", username, source.Code);
                    continue;
                }
                if (o.AutoJoin && channel.flags.HasFlag(Channel.Flags.left))
                {
                    await GetTelegramRpcAsync(() => client.Channels_JoinChannel(channel), $"join @{username}", ct);
                    logger.LogInformation("Telegram: joined @{Username}", username);
                }
                _channels[channel.id] = (source, username, channel.title, SubscriberCount(channel));
                resolvedChannels.Add((channel, source, username));
            }
            catch (RpcException ex)
            {
                logger.LogWarning(ex, "Telegram: cannot set up @{Username} ({Source})", username, source.Code);
                await states.MarkFailureAsync(source.SourceId, ex.Message, ct);
            }
        }
        // All configured channels are registered before history calls begin, so UpdateManager can durably ingest live
        // posts for every resolved channel while the backfill scheduler runs.
        foreach (var (channel, source, username) in resolvedChannels)
        {
            await BackfillAsync(client, channel, source, username, ct);
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
        var history = await GetTelegramRpcAsync(() => client.Messages_GetHistory(channel, limit: Math.Clamp(_o.BackfillLimit, 1, 100), min_id: minId), "recent backfill", ct);
        var messages = history.Messages.OfType<Message>().OrderBy(m => m.id).ToList();
        var stored = 0;
        foreach (var m in messages)
        {
            // The checkpoint travels with the message (ids are monotonic within a channel): a crash between them cannot
            // move the cursor past an unpublished post.
            if (await StoreAsync(m, source, username, ct, SubscriberCount(channel), channel.title, checkpoint: new CollectorCheckpoint(m.id.ToString(), ToUtc(m.date))))
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
        var pending = new List<TelegramBackfillJob>();
        foreach (var (channelId, (source, username, _, _)) in _channels)
        {
            var state = await states.GetAsync(source.SourceId, ct);
            var cursor = ReadCursor(state.Cursor);
            if (cursor is { Done: true } && cursor.Since <= since)
            {
                continue;
            }
            var channel = await ResolveAsync(client, username, ct);
            if (channel is null || channel.id != channelId)
            {
                continue;
            }
            var historyState = cursor is null || cursor.Since > since ? TelegramHistoryState.Start(since) : cursor;
            pending.Add(new TelegramBackfillJob
            {
                Channel = channel,
                Source = source,
                Username = username,
                State = historyState.Normalize(),
                Weight = Math.Clamp(source.Priority, 1, 10),
            });
        }
        if (pending.Count == 0)
        {
            return;
        }
        _loadingHistory = true;
        await reprocess.PauseAsync($"history load: {pending.Count} channel(s) since {since:yyyy-MM-dd}", ct);
        try
        {
            var scheduler = new TelegramBackfillScheduler(clock, _o.HistoryWorkers);
            await scheduler.RunAsync(pending, (job, schedulerCt) => LoadHistoryPageAsync(client, job, schedulerCt), ct);
            // Everything is in PostgreSQL: rebuild in order. Processing stayed paused so no historical message could be
            // handled before an earlier message from the same load was stored.
            await settings.SetStatusAsync(StatusKey, "history: rebuilding derived data", ct);
            var pendingCount = await reprocess.ResetAsync(ct);
            logger.LogInformation("Telegram: history load complete, {Count} raw message(s) are Pending for processing in order", pendingCount);
        }
        finally
        {
            _loadingHistory = false;
            await reprocess.ResumeAsync(CancellationToken.None);
        }
    }

    private async Task LoadHistoryPageAsync(WTelegram.Client client, TelegramBackfillJob job, CancellationToken ct)
    {
        const int limit = 100;
        var state = job.State;
        await settings.SetStatusAsync(StatusKey, $"history: @{job.Username} since {state.Since:yyyy-MM-dd}", ct);
        Messages_MessagesBase history;
        try
        {
            history = await GetTelegramRpcAsync(() => client.Messages_GetHistory(job.Channel, offset_id: Math.Max(1, state.LastId), add_offset: -limit, limit: limit), "history", ct, floodRetries: 0);
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            var retry = clock.GetUtcNow().AddSeconds(Math.Max(1, ex.X) + 1);
            job.State = state with { NextAttemptAt = retry, FloodWaitCount = state.FloodWaitCount + 1, LastFailureKind = "flood_wait" };
            await states.MarkSuccessAsync(job.Source.SourceId, null, null, WriteCursor(job.State), ct);
            logger.LogWarning("Telegram: @{Username} flood wait {Seconds}s; global gate is cooling down", job.Username, ex.X);
            return;
        }
        catch (TelegramRpcTimeoutException)
        {
            job.State = state with { NextAttemptAt = clock.GetUtcNow().AddMinutes(1), LastFailureKind = "timeout" };
            await states.MarkFailureAsync(job.Source.SourceId, "Telegram history RPC timeout; restarting the MTProto session.", ct);
            await states.MarkCursorAsync(job.Source.SourceId, WriteCursor(job.State), ct);
            throw;
        }
        catch (RpcException ex) when (IsTerminalHistoryError(ex))
        {
            job.State = state with { Done = true, NextAttemptAt = null, LastFailureKind = "terminal_rpc_error" };
            await states.MarkFailureAsync(job.Source.SourceId, ex.Message, ct);
            await states.MarkCursorAsync(job.Source.SourceId, WriteCursor(job.State), ct);
            logger.LogWarning(ex, "Telegram: @{Username} history stopped after terminal RPC error", job.Username);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            job.State = state with { NextAttemptAt = clock.GetUtcNow().AddMinutes(1), LastFailureKind = "rpc_error" };
            await states.MarkFailureAsync(job.Source.SourceId, ex.Message, ct);
            await states.MarkCursorAsync(job.Source.SourceId, WriteCursor(job.State), ct);
            return;
        }
        var page = history.Messages.OfType<Message>().Where(m => m.id > state.LastId).OrderBy(m => m.id).ToList();
        if (page.Count == 0)
        {
            job.State = state with { Done = true, NextAttemptAt = null, LastFailureKind = null };
            await states.MarkSuccessAsync(job.Source.SourceId, null, null, WriteCursor(job.State), ct);
            logger.LogInformation("Telegram: @{Username} history done since {Since:yyyy-MM-dd}: {Stored} stored, last id {LastId}", job.Username, state.Since, state.Stored, state.LastId);
            return;
        }
        var toStore = page.Where(m => ToUtc(m.date) >= state.Since).ToList();
        var lastId = page[^1].id;
        var next = state with { LastId = lastId, Stored = state.Stored + toStore.Count, Pages = state.Pages + 1, NextAttemptAt = null, LastFailureKind = null };
        // The whole page and its cursor commit in one transaction (ids are monotonic within a channel): a crash cannot
        // move the cursor past an unstored post, and 100 posts cost one round trip instead of 100. Service messages are
        // not stored but still move the cursor.
        var subscriberCount = SubscriberCount(job.Channel);
        var batch = toStore.Where(m => !IsServiceMessage(m)).Select(m => ToIncoming(m, job.Source, job.Username, subscriberCount, job.Channel.title)).ToList();
        var storedNow = await ingress.PublishBatchAsync(batch, job.Source, Name, new CollectorCheckpoint(null, ToUtc(page[^1].date), WriteCursor(next)), live: false, ct);
        job.State = next;
        if (job.State.Pages % 20 == 0)
        {
            logger.LogInformation("Telegram: @{Username} history … id {LastId} ({Date:yyyy-MM-dd HH:mm}), {Stored} stored", job.Username, lastId, ToUtc(page[^1].date), state.Stored + storedNow);
        }
    }

    private async Task<Channel?> ResolveAsync(WTelegram.Client client, string username, CancellationToken ct)
    {
        try
        {
            return (await GetTelegramRpcAsync(() => client.Contacts_ResolveUsername(username), $"resolve @{username} for history", ct)).Chat as Channel;
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "Telegram: cannot resolve @{Username} for the history load", username);
            return null;
        }
    }

    private static TelegramHistoryState? ReadCursor(JsonDocument? cursor)
    {
        if (cursor is null || !cursor.RootElement.TryGetProperty("history", out var h))
        {
            return null;
        }
        try
        {
            return h.Deserialize<TelegramHistoryState>()?.Normalize();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument WriteCursor(TelegramHistoryState cursor) => JsonSerializer.SerializeToDocument(new { history = cursor });

    /// <summary>
    /// One RPC through the request gate. A FLOOD_WAIT is retried a few times: the gate has already applied the
    /// cooldown, so the retry simply waits it out. Without this the ~300 startup calls (resolve + recent backfill for
    /// every channel) would crash the collector on the first flood and the restart would issue them all again.
    /// The history loop handles 420 itself (per-source NextAttemptAt) and never sees a retry here.
    /// </summary>
    private async Task<T> GetTelegramRpcAsync<T>(Func<Task<T>> rpc, string operation, CancellationToken ct, int floodRetries = 3)
    {
        var executor = _rpc ?? throw new InvalidOperationException("Telegram RPC executor is not initialized.");
        for (var attempt = 0; ; attempt++)
        {
            var started = clock.GetTimestamp();
            try
            {
                return await executor.ExecuteAsync(rpc, operation, ct);
            }
            catch (RpcException ex) when (ex.Code == 420 && attempt < floodRetries)
            {
                logger.LogWarning("Telegram: {Operation} flood wait {Seconds}s; retrying after the cooldown ({Attempt}/{Retries})", operation, ex.X, attempt + 1, floodRetries);
            }
            finally
            {
                logger.LogDebug("Telegram: {Operation} RPC took {Elapsed:N0} ms", operation, clock.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static bool IsTerminalHistoryError(RpcException ex) => ex.Code is 400 or 403 or 404;

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
        var (source, username, title, subscriberCount) = entry;
        // An edit does not move the id checkpoint (it belongs to an old post); the date still counts as activity.
        await StoreAsync(m, source, username, CancellationToken.None, subscriberCount, title, announceProcessor: !_loadingHistory,
            checkpoint: new CollectorCheckpoint(isEdit ? null : m.id.ToString(), ToUtc(m.date)));
    }

    /// <summary>Publishes one post (or edit); returns whether it was stored/accepted (false for service messages and known duplicates).</summary>
    private async Task<bool> StoreAsync(Message m, Source source, string username, CancellationToken ct, int? subscriberCount = null, string? channelTitle = null, bool announceProcessor = true, CollectorCheckpoint? checkpoint = null)
    {
        if (IsServiceMessage(m))
        {
            if (checkpoint is not null)
            {
                await states.MarkSuccessAsync(source.SourceId, checkpoint.LastSourceMessageId, checkpoint.LastMessageAt, checkpoint.Cursor, ct);
            }
            return false; // service messages still move the cursor
        }
        var result = await ingress.PublishAsync(ToIncoming(m, source, username, subscriberCount, channelTitle), source, Name, checkpoint, live: announceProcessor, ct);
        return result.Stored;
    }

    private static bool IsServiceMessage(Message m) => string.IsNullOrWhiteSpace(m.message) && m.media is null;

    private static IncomingMessage ToIncoming(Message m, Source source, string username, int? subscriberCount, string? channelTitle)
    {
        var payload = TelegramMessagePayload.From(m, username, subscriberCount, channelTitle);
        var revision = payload.EditDate is null ? "0" : $"e{payload.EditDate.Value.ToUnixTimeSeconds()}";
        return new IncomingMessage
        {
            SourceId = source.SourceId,
            SourceMessageId = revision == "0" ? m.id.ToString() : $"{m.id}:{revision}",
            SourceMessageKey = m.id.ToString(),
            SourceRevision = revision,
            PublishedAt = payload.EditDate ?? payload.Date,
            RawText = m.message,
            RawPayload = payload.ToDocument(),
            Url = $"https://t.me/{username}/{m.id}",
        };
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

    private static int? SubscriberCount(Channel channel) => channel.participants_count > 0 ? channel.participants_count : null;

    private static DateTimeOffset ToUtc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
