using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.Common;

namespace DshSuperagent.Providers.Feishu;

public sealed class FeishuSourceProvider(IProviderCommandRunner commands) : IBackgroundSourceProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _sourceLocks = new();
    public const string ProviderId = "feishu";
    public string Id => ProviderId;
    public string DisplayName => "飞书（原版 CLI 接入）";
    public string Description => "使用 strict-mode bot 的 lark-cli 接收事件、补收历史及回复订阅线程，不需要公网回调。";
    public SourceCapabilities Capabilities { get; } = new(ProactiveReply: true);
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("cli", "机器人 lark-cli", ProviderFieldType.Text, Required: true, DefaultValue: "lark-cli",
            Help: "可执行文件名或绝对路径；须提前登录机器人，并设置 config strict-mode bot。不是命令行字符串。"),
        new("chats", "接收的群（JSON）", ProviderFieldType.TextArea, DefaultValue: "[]",
            Help: "例如 [{\"chatId\":\"oc_example\",\"listen\":\"mentions\"}]；listen 支持 mentions/all。接收范围不替代 Policy 准入。"),
        new("directMessages", "接收私聊", ProviderFieldType.Bool, DefaultValue: "true"),
        new("otherChats", "其他群", ProviderFieldType.Select, DefaultValue: "none",
            Options: [new("none", "不接收"), new("mentions", "仅 @机器人")]),
        new("readPageSize", "历史读取每页条数", ProviderFieldType.Number, DefaultValue: "50"),
    ];

    public Task<SourceCallbackResult> HandleCallbackAsync(Source source, ProviderCallbackRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceCallbackResult { StatusCode = 405, Body = "飞书使用 CLI 事件流，不接受 HTTP 注入。" });

    private sealed record Bot(string Id, string AppId);
    private static Dictionary<string, string> Chats(Source source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = SourceJson.Parse(source.GetConfig("chats") ?? "[]");
        if (json.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("chats 必须是 JSON 数组。");
        foreach (var row in json.EnumerateArray())
        {
            var id = row.String("chatId") ?? "";
            var listen = row.String("listen") ?? "mentions";
            if (!Regex.IsMatch(id, "^oc_[A-Za-z0-9]+$") || listen is not ("mentions" or "all") || !result.TryAdd(id, listen))
                throw new InvalidOperationException("飞书群 ID、listen 或重复群配置无效。");
        }
        if (source.GetConfig("otherChats") is { } other && other is not ("none" or "mentions"))
            throw new InvalidOperationException("otherChats 只支持 none 或 mentions。");
        return result;
    }

    private async Task<Bot> ResolveBotAsync(Source source, CancellationToken token)
    {
        _ = Chats(source);
        var raw = await commands.RunAsync(new(source.Required("cli"), ["auth", "status"]), token);
        var start = raw.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("lark-cli auth status 未返回应用身份。");
        var auth = SourceJson.Parse(raw[start..]);
        if (auth.Child("identities").Child("bot").String("status") != "ready" || auth.String("appId") is not { Length: > 0 } appId)
            throw new InvalidOperationException("lark-cli 机器人身份尚未就绪。");
        var mode = await commands.RunAsync(new(source.Required("cli"), ["config", "strict-mode"]), token);
        if (!Regex.IsMatch(mode, @"strict-mode:\s*bot\b"))
            throw new InvalidOperationException("飞书 CLI 必须限制为 strict-mode bot，不能允许用户身份。");
        var info = await commands.RunAsync(new(source.Required("cli"),
            ["api", "GET", "/open-apis/bot/v3/info", "--as", "bot", "--format", "ndjson"]), token);
        var bot = SourceJson.Parse(info.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).Child("bot");
        return new Bot(bot.String("open_id") ?? throw new InvalidOperationException("无法读取机器人 open_id。"), appId);
    }

    public async Task<ProviderCheckResult> CheckAsync(Source source, CancellationToken cancellationToken)
    {
        try
        {
            var bot = await ResolveBotAsync(source, cancellationToken);
            return new(true, "飞书 CLI 机器人身份及 strict-mode bot 检查通过。", $"应用 {bot.AppId}，机器人 {bot.Id}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, "飞书配置检查失败。", ex.Message); }
    }

    public async Task RunAsync(Source source, ISourceRuntime runtime, CancellationToken cancellationToken)
    {
        var bot = await ResolveBotAsync(source, cancellationToken);
        var identity = bot.AppId + ":" + bot.Id;
        var previous = await runtime.ReadStateAsync("identity", cancellationToken);
        if (previous is not null && previous != identity) throw new InvalidOperationException("飞书应用或机器人身份已改变，请新建 Source，不能混用旧线程历史。");
        await runtime.WriteStateAsync("identity", identity, cancellationToken);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = WatchAsync(source, bot, runtime, stopped.Token);
        var history = RecoverAsync(source, bot, runtime, stopped.Token);
        try { await Task.WhenAny(watch, history); }
        finally { await stopped.CancelAsync(); }
        try { await Task.WhenAll(watch, history); }
        finally
        {
            if (_sourceLocks.TryRemove(source.Id, out var gate)) gate.Dispose();
        }
    }

    private async Task WatchAsync(Source source, Bot bot, ISourceRuntime runtime, CancellationToken token)
    {
        var retry = 1;
        while (!token.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await commands.WatchAsync(new(source.Required("cli"), ["event", "consume", "im.message.receive_v1", "--as", "bot"]),
                    async (line, ct) =>
                    {
                        JsonElement json;
                        try { json = SourceJson.Parse(line); }
                        catch (JsonException)
                        {
                            await runtime.ReportAsync("events", "error", "事件行不是有效 JSON，将由历史补收弥补。", ct);
                            return;
                        }
                        InboundEvent? inbound;
                        try
                        {
                            var root = json.String("thread_id") is { Length: > 0 } topic
                                ? await runtime.ReadStateAsync("topic-root:" + json.String("chat_id") + ":" + topic, ct) : null;
                            inbound = Normalize(json, bot.Id, bot.AppId, rootHistory: root);
                        }
                        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
                        {
                            await runtime.ReportAsync("events:item", "error", "跳过无法标准化的消息；未执行领域操作。", ct);
                            return;
                        }
                        if (inbound is not null) await ReceiveAsync(source, inbound, runtime, ct);
                    },
                    async (line, ct) =>
                    {
                        if (line.StartsWith("[event] ready", StringComparison.Ordinal))
                            await runtime.ReportAsync("events", "ready", "已连接飞书机器人事件流。", ct);
                    }, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await runtime.ReportAsync("events", "error", ex.Message, token);
            }
            retry = DateTimeOffset.UtcNow - started >= TimeSpan.FromMinutes(1) ? 1 : Math.Min(retry * 2, 60);
            await Task.Delay(TimeSpan.FromSeconds(retry), token);
        }
    }

    private async Task ReceiveAsync(Source source, InboundEvent inbound, ISourceRuntime runtime, CancellationToken token)
    {
        var chats = Chats(source);
        var chat = inbound.Channel.Get("chatId")!;
        var subscribed = (await runtime.GetSubscribedThreadsAsync(token)).Any(thread => thread.Key == inbound.ThreadKey);
        var listened = inbound.IsDirect ? source.Option("directMessages", true)
            : chats.TryGetValue(chat, out var listen) ? listen == "all" || inbound.Addressed || subscribed
            : source.GetConfig("otherChats") == "mentions" && (inbound.Addressed || subscribed);
        if (!listened) return;
        var gate = _sourceLocks.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            await runtime.WriteStateAsync("chat:" + chat, inbound.IsDirect ? "direct" : "group", token);
            if (await runtime.ReadStateAsync("history:" + chat, token) is null)
                await runtime.WriteStateAsync("history:" + chat, (inbound.ReceivedAt ?? DateTimeOffset.UtcNow).AddSeconds(-5).ToString("O"), token);
            if (inbound.Channel.Get("threadId") is { Length: > 0 } topic)
            {
                var key = "topic:" + chat + ":" + topic;
                var seen = await runtime.ReadStateAsync(key, token);
                var at = inbound.ReceivedAt ?? DateTimeOffset.UtcNow;
                if (seen is null || SourceJson.Time(seen) < at) await runtime.WriteStateAsync(key, at.ToString("O"), token);
                var rootKey = "topic-root:" + chat + ":" + topic;
                if (await runtime.ReadStateAsync(rootKey, token) is null)
                    await runtime.WriteStateAsync(rootKey, inbound.Channel.Get("rootId"), token);
            }
            await runtime.ReceiveAsync(inbound, token);
        }
        finally { gate.Release(); }
    }

    private async Task RecoverAsync(Source source, Bot bot, ISourceRuntime runtime, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var chats = Chats(source).ToDictionary(pair => pair.Key, _ => false);
                var followed = await runtime.GetSubscribedThreadsAsync(token);
                foreach (var thread in followed)
                    if (thread.Address.Get("chatId") is { } chat)
                        chats[chat] = thread.Address.Get("direct") == "true";
                foreach (var pair in await runtime.ReadStatePrefixAsync("chat:", token))
                    if (pair.Value == "direct") chats[pair.Key["chat:".Length..]] = true;
                foreach (var (chat, direct) in chats)
                {
                    if (direct && !source.Option("directMessages", true)) continue;
                    var until = DateTimeOffset.UtcNow;
                    var key = "history:" + chat;
                    var stored = await runtime.ReadStateAsync(key, token);
                    if (stored is null) { await runtime.WriteStateAsync(key, until.ToString("O"), token); continue; }
                    await ScanAsync(source, bot, runtime, chat, direct, "chat", chat, SourceJson.Time(stored), until, token);
                    var prefix = "topic:" + chat + ":";
                    foreach (var topic in await runtime.ReadStatePrefixAsync(prefix, token))
                    {
                        if (SourceJson.Time(topic.Value) < until.AddDays(-14))
                        {
                            var gate = _sourceLocks.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
                            await gate.WaitAsync(token);
                            try
                            {
                                var latest = await runtime.ReadStateAsync(topic.Key, token);
                                if (latest is not null && SourceJson.Time(latest) < until.AddDays(-14))
                                {
                                    await runtime.WriteStateAsync(topic.Key, null, token);
                                    await runtime.WriteStateAsync("topic-root:" + chat + ":" + topic.Key[prefix.Length..], null, token);
                                }
                            }
                            finally { gate.Release(); }
                            continue;
                        }
                        await ScanAsync(source, bot, runtime, chat, direct, "thread", topic.Key[prefix.Length..],
                            SourceJson.Time(stored), until, token);
                    }
                    await runtime.WriteStateAsync(key, until.AddSeconds(-5).ToString("O"), token);
                }
                foreach (var thread in followed)
                {
                    var chat = thread.Address.Get("chatId");
                    var message = thread.Address.Get("messageId");
                    if (chat is null || message is null) continue;
                    var meta = await ApiAsync(source, "/open-apis/im/v1/messages/mget", new { message_ids = new[] { message } }, token);
                    var item = meta.Child("items").Items().FirstOrDefault(item => item.String("message_id") == message);
                    if (item.String("chat_id") != chat || item.Flag("deleted")) continue;
                    if (item.String("thread_id") is not { Length: > 0 } topic) continue;
                    var key = "thread:" + thread.Key;
                    var until = DateTimeOffset.UtcNow;
                    var stored = await runtime.ReadStateAsync(key, token);
                    await ScanAsync(source, bot, runtime, chat, thread.Address.Get("direct") == "true", "thread", topic,
                        stored is null ? thread.CreatedAt.AddSeconds(-5) : SourceJson.Time(stored), until, token, thread.Address.Get("rootId") ?? message);
                    await runtime.WriteStateAsync(key, until.AddSeconds(-5).ToString("O"), token);
                }
                await runtime.ReportAsync("history", "ready", "已补收配置群、已知私聊及订阅话题。", token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await runtime.ReportAsync("history", "error", ex.Message, token);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }

    private async Task ScanAsync(Source source, Bot bot, ISourceRuntime runtime, string chat, bool direct,
        string kind, string container, DateTimeOffset since, DateTimeOffset until, CancellationToken token, string? rootHistory = null)
    {
        string? page = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        do
        {
            if (++pages > 200) throw new InvalidOperationException("飞书历史分页超过 200 页，读取游标未推进。");
            var result = await ApiAsync(source, "/open-apis/im/v1/messages", new
            {
                container_id_type = kind, container_id = container, start_time = since.ToUnixTimeSeconds().ToString(),
                end_time = until.ToUnixTimeSeconds().ToString(), sort_type = "ByCreateTimeAsc",
                page_size = source.Number("readPageSize", 50, 1, 50), page_token = page,
            }, token);
            if (result.Child("items").ValueKind != JsonValueKind.Array) throw new InvalidOperationException("飞书历史响应缺少 items。");
            foreach (var item in result.Child("items").EnumerateArray())
            {
                if (item.String("chat_id") != chat || (kind == "thread" && item.String("thread_id") != container))
                    throw new InvalidOperationException("飞书历史响应超出请求的群或话题范围。");
                if (item.Flag("deleted")) continue;
                InboundEvent? inbound;
                try
                {
                    var root = rootHistory ?? (item.String("thread_id") is { Length: > 0 } topic
                        ? await runtime.ReadStateAsync("topic-root:" + chat + ":" + topic, token) : null);
                    inbound = Normalize(item, bot.Id, bot.AppId, direct, root);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
                {
                    await runtime.ReportAsync("history:item", "error", "跳过无法标准化的历史消息；其他消息继续处理。", token);
                    continue;
                }
                if (inbound is not null) await ReceiveAsync(source, inbound, runtime, token);
            }
            page = result.Flag("has_more") ? result.String("page_token") : null;
            if (result.Flag("has_more") && (string.IsNullOrEmpty(page) || !seen.Add(page)))
                throw new InvalidOperationException("飞书历史分页未前进，读取游标未推进。");
        } while (page is not null);
    }

    private async Task<JsonElement> ApiAsync(Source source, string path, object parameters, CancellationToken token) =>
        SourceJson.Parse(await commands.RunAsync(new(source.Required("cli"),
            ["api", "GET", path, "--params", JsonSerializer.Serialize(parameters), "--as", "bot", "--format", "json"]), token));

    public static InboundEvent? Normalize(JsonElement message, string botId, string appId, bool? directHistory = null, string? rootHistory = null)
    {
        var sender = message.String("sender_id") ?? message.Child("sender").String("open_bot_id") ?? message.Child("sender").String("id");
        if (sender == botId || sender == appId || message.Flag("deleted")) return null;
        var id = message.String("message_id") ?? throw new FormatException("飞书事件缺少 message_id。");
        var chat = message.String("chat_id") ?? throw new FormatException("飞书事件缺少 chat_id。");
        if (!Regex.IsMatch(id, "^om_[A-Za-z0-9_-]+$") || !Regex.IsMatch(chat, "^oc_[A-Za-z0-9]+$") || string.IsNullOrWhiteSpace(sender))
            throw new FormatException("飞书消息或发送者标识无效。");
        var direct = directHistory ?? message.String("chat_type") == "p2p";
        var text = message.String("content") ?? message.Child("body").String("content") ?? "";
        try { text = SourceJson.Parse(text).String("text") ?? text; }
        catch (JsonException) { }
        var mentioned = false;
        foreach (var mention in message.Child("mentions").Items())
        {
            var mine = mention.String("id") == botId || mention.String("id") == appId;
            mentioned |= mine;
            if (mention.String("key") is { Length: > 0 } key)
                text = text.Replace(key, mine ? "" : "@" + (mention.String("name") ?? mention.String("id")), StringComparison.Ordinal);
        }
        var root = message.String("root_id");
        if (string.IsNullOrWhiteSpace(root)) root = string.IsNullOrWhiteSpace(rootHistory) ? id : rootHistory;
        var threadKey = chat + ":" + root;
        var address = ChannelAddress.Create(ProviderId, ("chatId", chat), ("messageId", id), ("rootId", root),
            ("threadId", message.String("thread_id")), ("direct", direct ? "true" : "false"), ("threadKey", threadKey));
        var kind = message.String("sender_type") ?? message.Child("sender").String("sender_type");
        return new InboundEvent("feishu:message:" + id, threadKey, "飞书 " + chat,
            new TriggerActor(sender!, sender!, kind == "user" ? ActorKind.Human : ActorKind.Bot), text.Trim(),
            direct || mentioned, direct, chat, address, PayloadJson: message.GetRawText(),
            ReceivedAt: SourceJson.Time(message.String("create_time")));
    }

    public async Task SendAsync(Source source, ChannelAddress address, string markdown, CancellationToken cancellationToken)
    {
        if (address.Kind != ProviderId || address.Get("messageId") is not { Length: > 0 } message
            || !Regex.IsMatch(message, "^om_[A-Za-z0-9_-]+$") || address.Get("deliveryId") is not { Length: > 0 } delivery)
            throw new InvalidOperationException("飞书回复缺少有效消息地址或稳定投递 ID。");
        if (Encoding.UTF8.GetByteCount(markdown) > 20_000) throw new InvalidOperationException("飞书回复超过 20 KB，请缩短内容。");
        try
        {
            await commands.RunAsync(new(source.Required("cli"), ["im", "+messages-reply", "--message-id", message,
                "--markdown", markdown, "--reply-in-thread", "--idempotency-key", delivery, "--as", "bot", "--format", "json"]), cancellationToken);
        }
        catch (ProviderCommandException ex) when (ex.ApiCode == 230011)
        {
            throw new ChannelDestinationGoneException("飞书目标消息已撤回或删除。");
        }
    }
}
