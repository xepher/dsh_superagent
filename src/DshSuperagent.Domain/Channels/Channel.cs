using DshSuperagent.Domain.Common;

namespace DshSuperagent.Domain.Channels;

public enum ChannelPurpose
{
    ChatReply,
    PullRequest,
    None,
}

public enum MessageStatus
{
    Pending,
    Sending,

    /// <summary>内容已交给 Provider 保存，等待目标客户端主动拉取；不代表对方已经收到。</summary>
    AwaitingPull,

    /// <summary>Provider 已确认送达。</summary>
    Sent,
    Failed,
    Dropped,
}

public enum MessagePurpose
{
    Ack,
    Answer,
    Result,
    Notice,
    Error,
}

/// <summary>
/// The write side of a Thread. Its address is provider-specific data (for example a WeCom stream id
/// or response_url); the domain only requires that a message can be stored before it is sent.
/// </summary>
public sealed class Channel : AggregateRoot
{
    private Channel()
    {
    }

    public Channel(Guid threadId, Guid sourceId, string providerId, string addressJson, ChannelPurpose purpose)
    {
        Id = Guid.NewGuid();
        ThreadId = threadId;
        SourceId = sourceId;
        ProviderId = providerId;
        AddressJson = addressJson;
        Purpose = purpose;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid ThreadId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ProviderId { get; private set; } = string.Empty;
    public string AddressJson { get; private set; } = "{}";
    public ChannelPurpose Purpose { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class ChannelMessage : Entity
{
    // SendingAt is distinct from CreatedAt; recovery must not reset a message that just started sending.
    private ChannelMessage()
    {
    }

    public ChannelMessage(
        Guid channelId,
        Guid? ticketId,
        MessagePurpose purpose,
        string content)
    {
        Id = Guid.NewGuid();
        ChannelId = channelId;
        TicketId = ticketId;
        Purpose = purpose;
        Content = content;
        Status = MessageStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
        NextAttemptAt = CreatedAt;
    }

    public Guid ChannelId { get; private set; }
    public Guid? TicketId { get; private set; }
    public MessagePurpose Purpose { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public MessageStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? SendingAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DroppedAt { get; private set; }

    public void BeginSending()
    {
        Status = MessageStatus.Sending;
        SendingAt = DateTimeOffset.UtcNow;
    }

    public void MarkDelivered()
    {
        Status = MessageStatus.Sent;
        SentAt = DateTimeOffset.UtcNow;
        SendingAt = null;
        LastError = null;
    }

    /// <summary>
    /// The transport stored the content but the target must still pull it. Storing is not delivery,
    /// so this status must not be retried and must not be reported as sent.
    /// </summary>
    public void MarkAwaitingPull()
    {
        Status = MessageStatus.AwaitingPull;
        SendingAt = null;
        LastError = null;
    }

    public void MarkFailed(string error, TimeSpan retryDelay)
    {
        Attempts++;
        LastError = error;
        Status = MessageStatus.Failed;
        SendingAt = null;
        NextAttemptAt = DateTimeOffset.UtcNow + retryDelay;
    }

    public void Defer(TimeSpan delay)
    {
        if (Status == MessageStatus.Pending)
        {
            NextAttemptAt = DateTimeOffset.UtcNow + delay;
        }
    }

    public void RequeueAfterFailure()
    {
        if (Status == MessageStatus.Failed && Attempts < 3)
        {
            Status = MessageStatus.Pending;
        }
    }

    public void RecoverStuckSending()
    {
        if (Status == MessageStatus.Sending)
        {
            Status = MessageStatus.Pending;
            SendingAt = null;
            NextAttemptAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkDropped(string reason)
    {
        Status = MessageStatus.Dropped;
        LastError = reason;
        SendingAt = null;
        DroppedAt = DateTimeOffset.UtcNow;
    }
}
