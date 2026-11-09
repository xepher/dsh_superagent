using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using Microsoft.Extensions.Logging;

namespace DshSuperagent.Providers.WeCom;

/// <summary>
/// WeCom is only one Source/Channel provider. The domain sees a Source, an InboundEvent and a
/// ChannelAddress; it never sees WeCom encryption, mention syntax or response_url.
/// </summary>
public sealed class WeComAibotSourceProvider : ISourceProvider
{
    public const string ProviderId = "wecom-aibot";
    private const int MaxContentBytes = 20_480;

    // WeCom parses the encrypted envelope and its plaintext itself; keep raw UTF-8 and unescaped
    // base64 ('+' must stay '+') exactly like the reference SDKs do.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStreamReplyStore _streamReplies;
    private readonly ILogger<WeComAibotSourceProvider>? _logger;

    public WeComAibotSourceProvider(
        IHttpClientFactory httpClientFactory,
        IStreamReplyStore streamReplies,
        ILogger<WeComAibotSourceProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _streamReplies = streamReplies;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string DisplayName => "企业微信智能机器人";
    public string Description => "企业微信自建智能机器人回调（加密 JSON + response_url / 流式回复）。";
    public SourceCapabilities Capabilities => new(
        Callback: true,
        EncryptedCallback: true,
        Streaming: true,
        ProactiveReply: true,
        SingleUseReply: true);

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("token", "回调 Token", ProviderFieldType.Secret, Required: true,
            Help: "企业微信智能机器人「接收消息」配置中的 Token。"),
        new("encodingAesKey", "EncodingAESKey", ProviderFieldType.Secret, Required: true,
            Help: "43 位 EncodingAESKey，用于验证和解密回调。"),
        new("botName", "机器人名称", ProviderFieldType.Text, Required: true, DefaultValue: "超级智能体",
            Help: "群聊中必须 @ 这个名称才会响应；请与企微后台名称保持一致。"),
        new("mentionAliases", "额外 @ 别名", ProviderFieldType.Text,
            Help: "可选，多个别名用逗号分隔。"),
        new("aibotId", "AibotId（可选）", ProviderFieldType.Text,
            Help: "填写后只接受该智能机器人 ID 的回调，防止串号。"),
        new("replyMode", "回复方式", ProviderFieldType.Select, DefaultValue: "stream",
            Options: [new("stream", "流式消息（推荐）"), new("response_url", "response_url 主动回复")],
            Help: "流式模式会立即回复处理中状态，并在完成时返回答案；response_url 模式只发送最终答案。"),
        new("responseHost", "response_url 主机白名单", ProviderFieldType.Text, DefaultValue: "https://qyapi.weixin.qq.com",
            Help: "仅允许向该 HTTPS 主机发送主动回复，避免 SSRF。"),
        new("receiveId", "ReceiveId（可选）", ProviderFieldType.Text,
            Help: "企业自建智能机器人通常留空。"),
    ];

    public async Task<SourceCallbackResult> HandleCallbackAsync(
        Source source,
        ProviderCallbackRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryReadSettings(source, out var settings, out var error))
            {
                return SourceCallbackResult.ServerError(error);
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return HandleVerification(request, settings);
            }

            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return SourceCallbackResult.BadRequest("仅支持 GET 验证和 POST 回调。");
            }

            return await HandleMessageAsync(source, request, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            _logger?.LogWarning("企微回调解析失败，异常类型 {ErrorType}", ex.GetType().Name);
            return SourceCallbackResult.BadRequest($"回调解析失败：{ex.Message}");
        }
    }

    public async Task<DeliveryOutcome> SendAsync(
        Source source,
        ChannelAddress address,
        string markdown,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(address.Kind, ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Channel 地址不属于 {ProviderId}。");
        }

        var content = TruncateUtf8(markdown, MaxContentBytes);
        if (address.Get("stream_id") is { Length: > 0 } streamId)
        {
            var existing = await _streamReplies.GetAsync(streamId, cancellationToken);
            if (existing?.Finished == true)
            {
                throw new ChannelDestinationGoneException("企微流式消息已结束，不能再写入回复。");
            }

            await _streamReplies.CompleteAsync(streamId, content, cancellationToken);
            _logger?.LogInformation(
                "企微流式内容已存就绪：Source {SourceId} Stream {StreamId}，等待企微刷新回调拉取；此状态不代表客户端已收到",
                source.Id, streamId);
            return DeliveryOutcome.AwaitingPull;
        }

        if (address.Get("response_url") is { Length: > 0 } responseUrl)
        {
            if (!TryReadSettings(source, out var settings, out var error))
            {
                throw new InvalidOperationException(error);
            }

            await PostResponseUrlAsync(settings, responseUrl, content, cancellationToken);
            return DeliveryOutcome.Delivered;
        }

        throw new InvalidOperationException("Channel 地址没有 stream_id 或 response_url，无法回复。");
    }

    public async Task CloseAsync(
        Source source,
        ChannelAddress address,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(address.Kind, ProviderId, StringComparison.OrdinalIgnoreCase)
            || address.Get("stream_id") is not { Length: > 0 } streamId)
        {
            return;
        }

        await _streamReplies.FinishAsync(streamId, cancellationToken);
        _logger?.LogInformation(
            "企微陈旧的流式应答口已收尾：Source {SourceId} Stream {StreamId}，内容保持原样",
            source.Id, streamId);
    }

    public Task<ProviderCheckResult> CheckAsync(Source source, CancellationToken cancellationToken)
    {
        if (!TryReadSettings(source, out var settings, out var error))
        {
            return Task.FromResult(new ProviderCheckResult(false, error));
        }

        return Task.FromResult(new ProviderCheckResult(
            true,
            "本地配置校验通过。保存后请在企业微信后台填写回调 URL 并完成 URL 验证。",
            $"回复方式：{settings.ReplyMode}；@ 名称：{string.Join(" / ", settings.Mentions)}"));
    }

    private SourceCallbackResult HandleVerification(ProviderCallbackRequest request, WeComSettings settings)
    {
        var signature = Query(request, "msg_signature");
        var timestamp = Query(request, "timestamp");
        var nonce = Query(request, "nonce");
        var echo = Query(request, "echostr");
        if (string.IsNullOrEmpty(echo))
        {
            return SourceCallbackResult.BadRequest("缺少 echostr。");
        }

        if (!WeComCrypto.VerifySignature(settings.Token, timestamp, nonce, echo, signature))
        {
            return SourceCallbackResult.BadRequest("回调签名校验失败。");
        }

        var plain = WeComCrypto.Decrypt(settings.EncodingAesKey, echo, settings.ReceiveId);
        return SourceCallbackResult.Text(plain);
    }

    private async Task<SourceCallbackResult> HandleMessageAsync(
        Source source,
        ProviderCallbackRequest request,
        WeComSettings settings,
        CancellationToken cancellationToken)
    {
        if (request.Body.Length > 1024 * 1024)
        {
            return SourceCallbackResult.BadRequest("回调报文超过 1 MB 限制。");
        }

        using var outer = JsonDocument.Parse(request.Body);
        var encrypt = outer.RootElement.TryGetProperty("encrypt", out var encryptElement)
            ? encryptElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(encrypt))
        {
            return SourceCallbackResult.BadRequest("回调缺少 encrypt 字段。");
        }

        var signature = Query(request, "msg_signature") ?? Query(request, "msgsignature");
        var timestamp = Query(request, "timestamp");
        var nonce = Query(request, "nonce") ?? WeComCrypto.NewNonce();
        if (!WeComCrypto.VerifySignature(settings.Token, timestamp, nonce, encrypt, signature))
        {
            return SourceCallbackResult.BadRequest("回调签名校验失败。");
        }

        var plain = WeComCrypto.Decrypt(settings.EncodingAesKey, encrypt, settings.ReceiveId);
        using var inner = JsonDocument.Parse(plain);
        var root = inner.RootElement;
        var messageType = GetString(root, "msgtype") ?? string.Empty;
        _logger?.LogInformation(
            "企微回调内容：MsgType {MsgType} ChatType {ChatType} ReplyMode {ReplyMode} AibotIdPresent {AibotIdPresent} AibotIdMatched {AibotIdMatched} ResponseUrlPresent {ResponseUrlPresent}",
            messageType,
            GetString(root, "chattype"),
            settings.ReplyMode,
            GetString(root, "aibotid") is { Length: > 0 },
            settings.AibotId is null || string.Equals(GetString(root, "aibotid"), settings.AibotId, StringComparison.Ordinal),
            GetString(root, "response_url") is { Length: > 0 });
        if (settings.AibotId is not null
            && GetString(root, "aibotid") is { } aibotId
            && !string.Equals(aibotId, settings.AibotId, StringComparison.Ordinal))
        {
            return EmptyAck();
        }

        if (string.Equals(messageType, "stream", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleStreamRefreshAsync(settings, root, nonce, cancellationToken);
        }

        if (string.Equals(messageType, "event", StringComparison.OrdinalIgnoreCase))
        {
            var eventType = root.TryGetProperty("event", out var eventElement)
                ? GetString(eventElement, "eventtype")
                : null;
            if (string.Equals(eventType, "enter_chat", StringComparison.OrdinalIgnoreCase))
            {
                var greeting = $"你好，我是 {settings.BotName}。在群里 @我 并描述要了解的代码功能，例如「用户列表页的分页逻辑在哪里？」。发送 /help 查看全部命令。";
                return EncryptedAck(settings, new JsonObject
                {
                    ["msgtype"] = "text",
                    ["text"] = new JsonObject { ["content"] = greeting },
                }, nonce);
            }

            return EmptyAck();
        }

        var rawText = ExtractText(root);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return EncryptedAck(settings, new JsonObject
            {
                ["msgtype"] = "text",
                ["text"] = new JsonObject { ["content"] = "目前支持文本问题；图片、文件等消息请转成文字后 @我。" },
            }, nonce);
        }

        var chatType = GetString(root, "chattype") ?? "single";
        var isDirect = string.Equals(chatType, "single", StringComparison.OrdinalIgnoreCase);
        var (isAddressed, cleaned) = StripMention(rawText, settings.Mentions);
        if (!isDirect && !isAddressed)
        {
            return EmptyAck();
        }

        cleaned = CleanQuestion(cleaned);
        if (cleaned.Length == 0)
        {
            return EncryptedAck(settings, new JsonObject
            {
                ["msgtype"] = "text",
                ["text"] = new JsonObject { ["content"] = $"请在 @{settings.BotName} 后输入具体问题。" },
            }, nonce);
        }

        var userId = root.TryGetProperty("from", out var from) ? GetString(from, "userid") : null;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return SourceCallbackResult.BadRequest("回调缺少 from.userid。");
        }

        var msgId = GetString(root, "msgid") ?? Guid.NewGuid().ToString("N");
        var responseUrl = GetString(root, "response_url");
        var chatId = GetString(root, "chatid");
        var address = ChannelAddress.Create(
            ProviderId,
            ("response_url", responseUrl),
            ("chat_id", chatId),
            ("user_id", userId));
        string ackBody;
        if (settings.ReplyMode == "response_url" && !string.IsNullOrWhiteSpace(responseUrl))
        {
            ackBody = string.Empty;
        }
        else
        {
            var streamId = Guid.NewGuid().ToString("N");
            address.Data["stream_id"] = streamId;
            var ackPacket = BuildStreamPacket(streamId, false, "已收到，正在分析代码仓库…");
            ackBody = EncryptPacket(settings, ackPacket, nonce);
            _logger?.LogInformation(
                "企微初始流式响应已构造：Stream {StreamId}，加密响应 {ResponseBytes} 字节",
                streamId, Encoding.UTF8.GetByteCount(ackBody));
        }

        var threadKey = isDirect ? $"wecom:direct:{userId}" : $"wecom:chat:{chatId ?? userId}";
        var threadTitle = isDirect ? $"{source.Name} 单聊 · {userId}" : $"{source.Name} 群聊 · {chatId}";
        var payload = JsonSerializer.Serialize(new
        {
            chat_type = chatType,
            chat_id = chatId,
            msg_type = messageType,
            msg_id = msgId,
        });

        return new SourceCallbackResult
        {
            ContentType = "application/json; charset=utf-8",
            Body = ackBody,
            AckBody = ackBody,
            AckContentType = ackBody.Length == 0 ? null : "application/json; charset=utf-8",
            RebindAck = persistedAck =>
            {
                if (persistedAck.Length == 0)
                {
                    return persistedAck;
                }

                try
                {
                    using var cached = JsonDocument.Parse(persistedAck);
                    if (cached.RootElement.ValueKind != JsonValueKind.Object
                        || !cached.RootElement.TryGetProperty("encrypt", out var cipher)
                        || cipher.GetString() is not { Length: > 0 } encrypted)
                    {
                        _logger?.LogWarning("企微重复回调：保存的回复不含 encrypt，按原样返回");
                        return persistedAck;
                    }

                    var rebound = WrapEncryptedPacket(settings, encrypted, nonce);
                    _logger?.LogInformation("企微重复回调：保留原回复内容和流式 ID，已按当前回调 nonce 重新签名");
                    return rebound;
                }
                catch (JsonException)
                {
                    _logger?.LogWarning("企微重复回调：保存的回复不是合法 JSON，按原样返回");
                    return persistedAck;
                }
            },
            Event = new InboundEvent(
                msgId,
                threadKey,
                threadTitle,
                new TriggerActor(userId, userId, ActorKind.Human),
                cleaned,
                isDirect || isAddressed,
                isDirect,
                isDirect ? "wecom:direct" : "wecom:group",
                address,
                Title: cleaned.Length > 60 ? cleaned[..60] : cleaned,
                PayloadJson: payload,
                CallbackAckBody: ackBody,
                CallbackAckContentType: ackBody.Length == 0 ? null : "application/json; charset=utf-8",
                ReceivedAt: DateTimeOffset.UtcNow),
        };
    }

    private async Task<SourceCallbackResult> HandleStreamRefreshAsync(
        WeComSettings settings,
        JsonElement root,
        string nonce,
        CancellationToken cancellationToken)
    {
        var streamId = root.TryGetProperty("stream", out var stream) ? GetString(stream, "id") : null;
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return SourceCallbackResult.BadRequest("流式刷新缺少 stream.id。");
        }

        var state = await _streamReplies.GetAsync(streamId, cancellationToken);
        _logger?.LogInformation(
            "企微流式刷新：Stream {StreamId} Found {Found} Finished {Finished}",
            streamId, state is not null, state?.Finished);
        var packet = state is null
            ? BuildStreamPacket(streamId, true, "会话已结束或过期，请重新 @我 提问。")
            : BuildStreamPacket(streamId, state.Finished, state.Content);
        var body = EncryptPacket(settings, packet, nonce);
        return new SourceCallbackResult
        {
            ContentType = "application/json; charset=utf-8",
            Body = body,
            AckBody = body,
            AckContentType = "application/json; charset=utf-8",
        };
    }

    private async Task PostResponseUrlAsync(
        WeComSettings settings,
        string responseUrl,
        string markdown,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.Equals(uri.Host, settings.ResponseHost.Host, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/cgi-bin/aibot/response", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("response_url 不在允许的企业微信主机和路径白名单内。");
        }

        var payload = JsonSerializer.Serialize(new
        {
            msgtype = "markdown",
            markdown = new { content = markdown },
        });
        var client = _httpClientFactory.CreateClient("wecom");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"企业微信回复失败：HTTP {(int)response.StatusCode}。");
        }

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var result = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (result.RootElement.ValueKind != JsonValueKind.Object
                || !result.RootElement.TryGetProperty("errcode", out var code)
                || code.ValueKind != JsonValueKind.Number
                || !code.TryGetInt32(out var errorCode))
            {
                throw new InvalidOperationException("企业微信回复失败：响应缺少有效的 errcode，不能确认发送成功。");
            }

            if (errorCode != 0)
            {
                throw new InvalidOperationException($"企业微信回复失败：errcode={errorCode}。");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("企业微信回复失败：响应不是合法 JSON，不能确认发送成功。");
        }

        _logger?.LogInformation("企微 response_url 回复已获接口确认：errcode=0");
    }

    private static bool TryReadSettings(Source source, out WeComSettings settings, out string error)
    {
        settings = default!;
        var token = source.GetConfig("token");
        var aesKey = source.GetConfig("encodingAesKey");
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Source 未配置回调 Token。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(aesKey))
        {
            error = "Source 未配置 EncodingAESKey。";
            return false;
        }

        try
        {
            _ = WeComCrypto.ParseKey(aesKey);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            error = ex.Message;
            return false;
        }

        var botName = string.IsNullOrWhiteSpace(source.GetConfig("botName")) ? "超级智能体" : source.GetConfig("botName")!.Trim();
        var mentions = new List<string> { botName };
        mentions.AddRange((source.GetConfig("mentionAliases") ?? string.Empty)
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var replyMode = source.GetConfig("replyMode");
        if (replyMode is not ("stream" or "response_url"))
        {
            replyMode = "stream";
        }

        var responseHostText = string.IsNullOrWhiteSpace(source.GetConfig("responseHost"))
            ? "https://qyapi.weixin.qq.com"
            : source.GetConfig("responseHost")!;
        if (!Uri.TryCreate(responseHostText, UriKind.Absolute, out var responseHost)
            || responseHost.Scheme != Uri.UriSchemeHttps
            || !responseHost.IsDefaultPort)
        {
            error = "response_url 主机白名单必须是合法的 HTTPS 地址。";
            return false;
        }

        settings = new WeComSettings(
            token,
            aesKey,
            botName,
            mentions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            source.GetConfig("aibotId")?.Trim(),
            replyMode,
            responseHost,
            source.GetConfig("receiveId") ?? string.Empty);
        error = string.Empty;
        return true;
    }

    private static (bool Addressed, string Text) StripMention(string text, IReadOnlyList<string> mentions)
    {
        var addressed = false;
        var result = text;
        foreach (var mention in mentions.OrderByDescending(value => value.Length))
        {
            var pattern = $@"@\s*{Regex.Escape(mention)}";
            if (Regex.IsMatch(result, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                addressed = true;
                result = Regex.Replace(result, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
        }

        return (addressed, result);
    }

    private static string CleanQuestion(string text)
    {
        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        return cleaned.TrimStart(':', '：', ',', '，', '-', ' ');
    }

    private static string? ExtractText(JsonElement root)
    {
        var type = GetString(root, "msgtype");
        var builder = new StringBuilder();
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("text", out var text))
        {
            builder.Append(GetString(text, "content"));
        }
        else if (string.Equals(type, "voice", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("voice", out var voice))
        {
            builder.Append(GetString(voice, "content"));
        }
        else if (string.Equals(type, "mixed", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("mixed", out var mixed)
                 && mixed.TryGetProperty("msg_item", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (string.Equals(GetString(item, "msgtype"), "text", StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("text", out var itemText))
                {
                    builder.AppendLine(GetString(itemText, "content"));
                }
            }
        }

        if (root.TryGetProperty("quote", out var quote) && quote.TryGetProperty("text", out var quoteText))
        {
            var quoted = GetString(quoteText, "content");
            if (!string.IsNullOrWhiteSpace(quoted))
            {
                builder.AppendLine();
                builder.AppendLine($"[引用消息] {quoted}");
            }
        }

        return builder.ToString();
    }

    private static SourceCallbackResult EmptyAck() => SourceCallbackResult.Ack(string.Empty);

    private static SourceCallbackResult EncryptedAck(WeComSettings settings, JsonObject packet, string nonce) =>
        SourceCallbackResult.Ack(EncryptPacket(settings, packet, nonce), "application/json; charset=utf-8");

    private static string EncryptPacket(WeComSettings settings, JsonObject packet, string nonce)
    {
        var plaintext = packet.ToJsonString(WireJson);
        var encrypted = WeComCrypto.Encrypt(settings.EncodingAesKey, plaintext, settings.ReceiveId);
        return WrapEncryptedPacket(settings, encrypted, nonce);
    }

    private static string WrapEncryptedPacket(WeComSettings settings, string encrypted, string nonce)
    {
        var timestamp = WeComCrypto.NewTimestamp();
        var signature = WeComCrypto.ComputeSignature(settings.Token, timestamp, nonce, encrypted);
        return JsonSerializer.Serialize(new
        {
            encrypt = encrypted,
            msgsignature = signature,
            timestamp = long.Parse(timestamp),
            nonce,
        }, WireJson);
    }

    private static JsonObject BuildStreamPacket(string streamId, bool finish, string content) =>
        new()
        {
            ["msgtype"] = "stream",
            ["stream"] = new JsonObject
            {
                ["id"] = streamId,
                ["finish"] = finish,
                ["content"] = content,
            },
        };

    private static string? Query(ProviderCallbackRequest request, string name) =>
        request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        const string suffix = "\n\n…（内容过长已截断）";
        var budget = maxBytes - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var size = Encoding.UTF8.GetByteCount(rune.ToString());
            if (used + size > budget)
            {
                break;
            }

            builder.Append(rune);
            used += size;
        }

        return builder + suffix;
    }

    private sealed record WeComSettings(
        string Token,
        string EncodingAesKey,
        string BotName,
        IReadOnlyList<string> Mentions,
        string? AibotId,
        string ReplyMode,
        Uri ResponseHost,
        string ReceiveId);
}
