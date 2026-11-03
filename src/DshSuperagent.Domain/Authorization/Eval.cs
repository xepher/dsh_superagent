using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Events;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Domain.Authorization;

public enum EvalStatus
{
    Queued,
    Running,
    Completed,
    Unavailable,
}

public enum DecisionKind
{
    Accept,
    Stop,
    Ignore,
    Unavailable,
}

/// <summary>
/// The result of an Eval. It states why work starts, continues or stops. Inferred work can be
/// marked "would accept" while the Policy is in observe mode, and then nothing is accepted.
/// </summary>
public sealed record Decision(
    DecisionKind Kind,
    TriggerStrength Strength,
    string Reason,
    IReadOnlyList<Guid> MatchedRuleIds,
    string? Scope,
    bool WouldAccept,
    string? AssessmentJson)
{
    public static Decision Accept(
        TriggerStrength strength,
        string reason,
        IEnumerable<Guid> matchedRuleIds,
        string scope,
        string? assessmentJson = null) =>
        new(DecisionKind.Accept, strength, reason, matchedRuleIds.ToList(), scope, false, assessmentJson);

    public static Decision Ignore(
        TriggerStrength strength,
        string reason,
        bool wouldAccept = false,
        string? assessmentJson = null) =>
        new(DecisionKind.Ignore, strength, reason, [], null, wouldAccept, assessmentJson);

    public static Decision Stop(string reason) =>
        new(DecisionKind.Stop, TriggerStrength.Explicit, reason, [], null, false, null);

    public static Decision Unavailable(string reason) =>
        new(DecisionKind.Unavailable, TriggerStrength.Inferred, reason, [], null, false, null);
}

public sealed class Eval : AggregateRoot
{
    // Recovery is bounded: three attempts, then the Eval becomes unavailable and releases the Thread.
    private readonly List<Guid> _triggerIds = [];

    private Eval()
    {
    }

    private Eval(Guid id, Guid threadId, Guid sourceId, int policyVersion, IEnumerable<Guid> triggerIds)
    {
        Id = id;
        ThreadId = threadId;
        SourceId = sourceId;
        PolicyVersion = policyVersion;
        _triggerIds.AddRange(triggerIds);
        Status = EvalStatus.Queued;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid ThreadId { get; private set; }
    public Guid SourceId { get; private set; }
    public int PolicyVersion { get; private set; }
    public EvalStatus Status { get; private set; }
    public IReadOnlyList<Guid> TriggerIds => _triggerIds;
    public int Attempts { get; private set; }
    public Decision? Decision { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsActive => Status is EvalStatus.Queued or EvalStatus.Running;

    public static Eval Start(Guid threadId, Guid sourceId, int policyVersion, IEnumerable<Guid> triggerIds) =>
        new(Guid.NewGuid(), threadId, sourceId, policyVersion, triggerIds);

    public void AddTrigger(Guid triggerId)
    {
        if (!_triggerIds.Contains(triggerId))
        {
            _triggerIds.Add(triggerId);
        }
    }

    public void Begin()
    {
        if (Status != EvalStatus.Queued)
        {
            throw new DomainException("只有排队中的 Eval 可以开始。");
        }

        Status = EvalStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void Complete(Decision decision)
    {
        if (!IsActive)
        {
            throw new DomainException("已结束的 Eval 不能再次完成。");
        }

        Decision = decision;
        Status = decision.Kind == DecisionKind.Unavailable ? EvalStatus.Unavailable : EvalStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        Raise(new EvalCompleted(Id, ThreadId, decision.Kind.ToString(), PolicyVersion, CompletedAt.Value));
    }

    public void MarkUnavailable(string reason)
    {
        if (!IsActive)
        {
            return;
        }

        Error = reason;
        Decision = Decision.Unavailable(reason);
        Status = EvalStatus.Unavailable;
        CompletedAt = DateTimeOffset.UtcNow;
        Raise(new EvalCompleted(Id, ThreadId, DecisionKind.Unavailable.ToString(), PolicyVersion, CompletedAt.Value));
    }

    /// <summary>Recovers an Eval that was Running when the host stopped. After three attempts it becomes unavailable.</summary>
    public bool TryRecoverAfterRestart(string reason)
    {
        if (Status != EvalStatus.Running)
        {
            return false;
        }

        Attempts++;
        if (Attempts >= 3)
        {
            MarkUnavailable(reason);
            return false;
        }

        Status = EvalStatus.Queued;
        StartedAt = null;
        CompletedAt = null;
        Error = reason;
        return true;
    }

    /// <summary>Bounds recovery attempts for an Eval that has stayed Queued without progress.</summary>
    public bool TryRecoverQueued(string reason)
    {
        if (Status != EvalStatus.Queued)
        {
            return false;
        }

        Attempts++;
        if (Attempts >= 3)
        {
            MarkUnavailable(reason);
            return false;
        }

        Error = reason;
        return true;
    }

    public void RequeueForPolicyChange(int newPolicyVersion, string reason)
    {
        Attempts++;
        PolicyVersion = newPolicyVersion;
        Error = reason;
        Decision = null;
        Status = EvalStatus.Queued;
        StartedAt = null;
        CompletedAt = null;
    }
}
