using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Domain.Threads;

public sealed class Trigger
{
    private Trigger()
    {
    }

    private Trigger(
        Guid id,
        Guid sourceId,
        Guid threadId,
        Guid? channelId,
        string externalId,
        string title,
        TriggerActor actor,
        string text,
        bool addressed,
        TriggerStrength strength,
        string receiptScope,
        string? payloadJson,
        string? callbackAckBody,
        string? callbackAckContentType,
        DateTimeOffset receivedAt)
    {
        Id = id;
        SourceId = sourceId;
        ThreadId = threadId;
        ChannelId = channelId;
        ExternalId = externalId;
        Title = title;
        Actor = actor;
        Text = text;
        Addressed = addressed;
        Strength = strength;
        ReceiptScope = receiptScope;
        PayloadJson = payloadJson;
        CallbackAckBody = callbackAckBody;
        CallbackAckContentType = callbackAckContentType;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid ThreadId { get; private set; }
    public Guid? ChannelId { get; private set; }

    /// <summary>The provider's event id. Unique per Source, which makes accepting a Trigger twice create one Ticket.</summary>
    public string ExternalId { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;
    public TriggerActor Actor { get; private set; } = new("", "", ActorKind.Human);
    public string Text { get; private set; } = string.Empty;
    public bool Addressed { get; private set; }
    public TriggerStrength Strength { get; private set; }
    public string ReceiptScope { get; private set; } = string.Empty;

    /// <summary>
    /// Source-specific facts for the Executor (for example a scan finding). It is input data, not authority.
    /// </summary>
    public string? PayloadJson { get; private set; }

    public string? CallbackAckBody { get; private set; }
    public string? CallbackAckContentType { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }

    public static Trigger Record(
        Guid sourceId,
        Guid threadId,
        Guid? channelId,
        string externalId,
        string title,
        TriggerActor actor,
        string text,
        bool addressed,
        TriggerStrength strength,
        string receiptScope,
        string? payloadJson,
        string? callbackAckBody,
        string? callbackAckContentType,
        DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new DomainException("Trigger 必须有 Source 提供的外部 ID。");
        }

        return new Trigger(
            Guid.NewGuid(),
            sourceId,
            threadId,
            channelId,
            externalId.Trim(),
            title.Trim(),
            actor,
            text,
            addressed,
            strength,
            receiptScope.Trim(),
            payloadJson,
            callbackAckBody,
            callbackAckContentType,
            receivedAt);
    }
}

public sealed class Subscription
{
    private Subscription()
    {
    }

    internal Subscription(Guid threadId, Guid ticketId, bool contextOnly)
    {
        Id = Guid.NewGuid();
        ThreadId = threadId;
        TicketId = ticketId;
        ContextOnly = contextOnly;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ThreadId { get; private set; }
    public Guid TicketId { get; private set; }

    /// <summary>A context-only subscription adds context and lends no authority.</summary>
    public bool ContextOnly { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DroppedAt { get; private set; }

    public bool IsLive => DroppedAt is null;

    internal void Drop() => DroppedAt = DateTimeOffset.UtcNow;

    internal void Reactivate() => DroppedAt = null;
}

/// <summary>
/// Threads order Triggers and own their subscriptions. At most one Eval runs on a Thread; Triggers
/// that arrive meanwhile wait in <see cref="PendingTriggerIds"/> for the next batch.
/// </summary>
public sealed class Thread : AggregateRoot
{
    private readonly List<Subscription> _subscriptions = [];
    private readonly List<Guid> _pendingTriggerIds = [];

    private Thread()
    {
    }

    private Thread(Guid id, Guid sourceId, string externalKey, string title)
    {
        Id = id;
        SourceId = sourceId;
        ExternalKey = externalKey;
        Title = title;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActivityAt = CreatedAt;
    }

    public Guid SourceId { get; private set; }
    public string ExternalKey { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public IReadOnlyList<Subscription> Subscriptions => _subscriptions;
    public Guid? ActiveEvalId { get; private set; }
    public IReadOnlyList<Guid> PendingTriggerIds => _pendingTriggerIds;

    public static Thread Open(Guid sourceId, string externalKey, string title)
    {
        if (string.IsNullOrWhiteSpace(externalKey))
        {
            throw new DomainException("Thread 的外部键不能为空。");
        }

        return new Thread(Guid.NewGuid(), sourceId, externalKey.Trim(), title.Trim());
    }

    public void Touch(DateTimeOffset at) => LastActivityAt = at;

    public bool HasSubscription(Guid ticketId) =>
        _subscriptions.Any(subscription => subscription.IsLive && subscription.TicketId == ticketId);

    public bool TrySubscribe(Guid ticketId, bool contextOnly)
    {
        var existing = _subscriptions.FirstOrDefault(subscription => subscription.TicketId == ticketId);
        if (existing is { IsLive: true })
        {
            return false;
        }

        if (existing is not null)
        {
            existing.Reactivate();
            return true;
        }

        _subscriptions.Add(new Subscription(Id, ticketId, contextOnly));
        return true;
    }

    public bool DropSubscription(Guid ticketId)
    {
        var existing = _subscriptions.FirstOrDefault(subscription => subscription.IsLive && subscription.TicketId == ticketId);
        if (existing is null)
        {
            return false;
        }

        existing.Drop();
        return true;
    }

    public void SetActiveEval(Guid evalId)
    {
        if (ActiveEvalId is not null && ActiveEvalId != evalId)
        {
            throw new DomainException("一个 Thread 同时只能运行一个 Eval。");
        }

        ActiveEvalId = evalId;
    }

    public void ClearActiveEval(Guid evalId)
    {
        if (ActiveEvalId == evalId)
        {
            ActiveEvalId = null;
        }
    }

    public void EnqueuePendingTrigger(Guid triggerId)
    {
        if (!_pendingTriggerIds.Contains(triggerId))
        {
            _pendingTriggerIds.Add(triggerId);
        }
    }

    public IReadOnlyList<Guid> TakePendingTriggers(int max = 20)
    {
        var taken = _pendingTriggerIds.Take(max).ToList();
        _pendingTriggerIds.RemoveRange(0, taken.Count);
        return taken;
    }
}

public enum DispositionKind
{
    Filtered,
    Ignored,
    Admitted,
    Evaluated,
    FannedOut,
    OwnedByTicket,
    Stopped,
}

public sealed class Disposition
{
    private Disposition()
    {
    }

    public Disposition(
        Guid triggerId,
        Guid threadId,
        DispositionKind kind,
        string reason,
        Guid? evalId = null,
        Guid? ticketId = null)
    {
        Id = Guid.NewGuid();
        TriggerId = triggerId;
        ThreadId = threadId;
        Kind = kind;
        Reason = reason;
        EvalId = evalId;
        TicketId = ticketId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid TriggerId { get; private set; }
    public Guid ThreadId { get; private set; }
    public DispositionKind Kind { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid? EvalId { get; private set; }
    public Guid? TicketId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Relates Threads that share a Trigger. Correlation adds context; it never routes.</summary>
public sealed class Correlation
{
    private Correlation()
    {
    }

    public Correlation(Guid triggerId, Guid threadId, string reason)
    {
        Id = Guid.NewGuid();
        TriggerId = triggerId;
        ThreadId = threadId;
        Reason = reason;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid TriggerId { get; private set; }
    public Guid ThreadId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
