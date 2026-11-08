using System.Text.Json;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Core.Providers.Sources;

public enum ProviderFieldType
{
    Text,
    Secret,
    Bool,
    Number,
    Select,
    TextArea,
}

public sealed record ProviderFieldOption(string Value, string Label);

public sealed record ProviderConfigField(
    string Key,
    string Label,
    ProviderFieldType Type,
    bool Required = false,
    string? Help = null,
    string? DefaultValue = null,
    IReadOnlyList<ProviderFieldOption>? Options = null,
    string? Placeholder = null);

public sealed record SourceCapabilities(
    bool Callback = false,
    bool EncryptedCallback = false,
    bool Streaming = false,
    bool ProactiveReply = false,
    bool Scheduled = false);

public sealed record ProviderCallbackRequest(
    string Method,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

public sealed class ChannelAddress
{
    public string Kind { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Data.TryGetValue(key, out var value) ? value : null;

    public static ChannelAddress Create(string kind, params (string Key, string? Value)[] values)
    {
        var address = new ChannelAddress { Kind = kind };
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                address.Data[key] = value;
            }
        }

        return address;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ChannelAddress FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ChannelAddress();
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelAddress>(json) ?? new ChannelAddress();
        }
        catch (JsonException)
        {
            return new ChannelAddress();
        }
    }
}

/// <summary>One provider-neutral event. The provider owns its transport and mention syntax; the domain owns admission.</summary>
public sealed record InboundEvent(
    string ExternalId,
    string ThreadKey,
    string ThreadTitle,
    TriggerActor Actor,
    string Text,
    bool Addressed,
    bool IsDirect,
    string ReceiptScope,
    ChannelAddress Channel,
    string? Title = null,
    string? PayloadJson = null,
    string? CallbackAckBody = null,
    string? CallbackAckContentType = null,
    DateTimeOffset? ReceivedAt = null);

public sealed record SourceCallbackResult
{
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public string Body { get; init; } = string.Empty;
    public InboundEvent? Event { get; init; }
    public string? AckBody { get; init; }
    public string? AckContentType { get; init; }

    /// <summary>Rebind a persisted acknowledgement to this callback's transport parameters without changing its logical reply.</summary>
    public Func<string, string>? RebindAck { get; init; }

    public static SourceCallbackResult Text(string text) => new()
    {
        ContentType = "text/plain; charset=utf-8",
        Body = text,
    };

    public static SourceCallbackResult Ack(string? body = null, string contentType = "application/json; charset=utf-8") => new()
    {
        Body = body ?? string.Empty,
        AckBody = body,
        AckContentType = contentType,
    };

    public static SourceCallbackResult BadRequest(string reason) => new()
    {
        StatusCode = 400,
        ContentType = "text/plain; charset=utf-8",
        Body = reason,
    };

    public static SourceCallbackResult ServerError(string reason) => new()
    {
        StatusCode = 500,
        ContentType = "text/plain; charset=utf-8",
        Body = reason,
    };
}

public sealed record ProviderCheckResult(bool Ok, string Message, string? Detail = null);

public interface ISourceProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }
    SourceCapabilities Capabilities { get; }

    Task<SourceCallbackResult> HandleCallbackAsync(
        Source source,
        ProviderCallbackRequest request,
        CancellationToken cancellationToken);

    Task SendAsync(
        Source source,
        ChannelAddress address,
        string markdown,
        CancellationToken cancellationToken);

    Task<ProviderCheckResult> CheckAsync(Source source, CancellationToken cancellationToken);
}

public sealed record StreamReplyState(string StreamId, bool Finished, string Content);

public interface IStreamReplyStore
{
    Task<StreamReplyState?> GetAsync(string streamId, CancellationToken cancellationToken);
    Task SetRunningAsync(string streamId, string content, CancellationToken cancellationToken);
    Task CompleteAsync(string streamId, string content, CancellationToken cancellationToken);
}

public sealed class SourceProviderRegistry
{
    private readonly Dictionary<string, ISourceProvider> _providers;

    public SourceProviderRegistry(IEnumerable<ISourceProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ISourceProvider> All => _providers.Values;

    public ISourceProvider? Find(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;

    public ISourceProvider GetRequired(string providerId) =>
        Find(providerId) ?? throw new InvalidOperationException($"未注册 Source Provider：{providerId}");
}
