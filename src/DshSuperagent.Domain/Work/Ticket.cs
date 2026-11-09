using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Domain.Work;

public enum MandateBasis
{
    ExplicitRequest,
    FollowUp,
    AdmissionCell,
    BotRule,
}

/// <summary>
/// What an accept grants. It is immutable except for the two operations that withdraw or revoke it:
/// a person's stop, and a failed revalidation against the current Policy.
/// </summary>
public sealed class Mandate : Entity
{
    private Mandate()
    {
    }

    private Mandate(
        Guid id,
        int policyVersion,
        TriggerStrength strength,
        string scope,
        IReadOnlyList<Guid> matchedRuleIds,
        TriggerActor requester,
        bool addressed,
        Guid sourceId,
        string receiptScope,
        MandateBasis basis)
    {
        Id = id;
        PolicyVersion = policyVersion;
        Strength = strength;
        Scope = scope;
        MatchedRuleIds = matchedRuleIds.ToList();
        Requester = requester;
        Addressed = addressed;
        SourceId = sourceId;
        ReceiptScope = receiptScope;
        Basis = basis;
        GrantedAt = DateTimeOffset.UtcNow;
    }

    public int PolicyVersion { get; private set; }
    public TriggerStrength Strength { get; private set; }
    public string Scope { get; private set; } = string.Empty;
    public List<Guid> MatchedRuleIds { get; private set; } = [];
    public TriggerActor Requester { get; private set; } = new("", "", ActorKind.Human);
    public bool Addressed { get; private set; }
    public Guid SourceId { get; private set; }
    public string ReceiptScope { get; private set; } = string.Empty;
    public MandateBasis Basis { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    public bool IsActive => WithdrawnAt is null && RevokedAt is null;

    public static Mandate Grant(
        int policyVersion,
        TriggerStrength strength,
        string scope,
        IEnumerable<Guid> matchedRuleIds,
        TriggerActor requester,
        bool addressed,
        Guid sourceId,
        string receiptScope,
        MandateBasis basis)
    {
        if (strength == TriggerStrength.NotAdmitted)
        {
            throw new DomainException("未被准入的候选不能获得 Mandate。");
        }

        return new Mandate(
            Guid.NewGuid(),
            policyVersion,
            strength,
            scope,
            matchedRuleIds.ToList(),
            requester,
            addressed,
            sourceId,
            receiptScope,
            basis);
    }

    public void WithdrawByPerson()
    {
        if (WithdrawnAt is null)
        {
            WithdrawnAt = DateTimeOffset.UtcNow;
        }
    }

    public void Revoke(string reason)
    {
        if (RevokedAt is null)
        {
            RevokedAt = DateTimeOffset.UtcNow;
            RevokedReason = reason;
        }
    }
}

public enum TicketStatus
{
    Open,
    Ended,
}

public enum TicketOutcome
{
    None,
    Completed,
    Dropped,
    Stopped,
    Failed,
}

public enum TicketStage
{
    PersonDelivery,
    PersonAnswer,
    PersonReview,
    PersonBlocked,
    PersonFailed,
    WorkingQueued,
    Idle,
    Working,
    Ended,
}

public enum WorkKind
{
    Answer,
    Fix,
}

public enum AssignmentStatus
{
    Pending,
    Running,
    Ended,
}

public enum InputKind
{
    Trigger,
    PolicyChange,
    Resume,
    Note,
    Retry,
    Answer,
}

public enum ReportKind
{
    Progress,
    Waiting,
    Ready,
    Blocked,
    Failed,
    Dropped,
}

public sealed class Assignment : Entity
{
    private Assignment()
    {
    }

    internal Assignment(Guid ticketId, string executorName)
    {
        Id = Guid.NewGuid();
        TicketId = ticketId;
        ExecutorName = executorName;
        Status = AssignmentStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid TicketId { get; private set; }
    public string ExecutorName { get; private set; } = string.Empty;
    public AssignmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public void Begin()
    {
        if (Status != AssignmentStatus.Pending)
        {
            throw new DomainException("只有待启动的 Assignment 可以开始。");
        }

        Status = AssignmentStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void End()
    {
        if (Status == AssignmentStatus.Ended)
        {
            return;
        }

        Status = AssignmentStatus.Ended;
        EndedAt = DateTimeOffset.UtcNow;
    }

    public void RecoverAfterRestart()
    {
        if (Status == AssignmentStatus.Running)
        {
            Status = AssignmentStatus.Pending;
            StartedAt = null;
        }
    }
}

public sealed class Input : Entity
{
    private Input()
    {
    }

    public Input(Guid ticketId, InputKind kind, string content, Guid? triggerId = null)
    {
        Id = Guid.NewGuid();
        TicketId = ticketId;
        Kind = kind;
        Content = content;
        TriggerId = triggerId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid TicketId { get; private set; }
    public InputKind Kind { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public Guid? TriggerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public string? DeliveryError { get; private set; }

    public void MarkDelivered()
    {
        DeliveredAt = DateTimeOffset.UtcNow;
        DeliveryError = null;
    }

    public void MarkRead() => ReadAt = DateTimeOffset.UtcNow;

    public void MarkDeliveryFailed(string error)
    {
        DeliveryError = error;
    }
}

public sealed class Report : Entity
{
    private Report()
    {
    }

    public Report(
        Guid ticketId,
        Guid assignmentId,
        ReportKind kind,
        string summary,
        string? humanAction = null,
        string? pullRequestUrl = null,
        string? artifactJson = null)
    {
        Id = Guid.NewGuid();
        TicketId = ticketId;
        AssignmentId = assignmentId;
        Kind = kind;
        Summary = summary;
        HumanAction = humanAction;
        PullRequestUrl = pullRequestUrl;
        ArtifactJson = artifactJson;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid TicketId { get; private set; }
    public Guid AssignmentId { get; private set; }
    public ReportKind Kind { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string? HumanAction { get; private set; }
    public string? PullRequestUrl { get; private set; }

    /// <summary>Stored work product such as a validated patch. It is not authority and cannot be replayed as an Input.</summary>
    public string? ArtifactJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// A unit of authorized work. Only an accepted Eval creates one; a person's follow-up can resume
/// an ended Ticket unless that person stopped it. The Ticket defines its own stage instead of
/// following the Executor Session state.
/// </summary>
public sealed class Ticket : AggregateRoot
{
    // Recovery resets only interrupted Assignments; ended Tickets and withdrawn Mandates stay closed.
    // Ticket state is owned by the domain, never copied from the Executor Session.
    private readonly List<Input> _inputs = [];
    private readonly List<Assignment> _assignments = [];
    private readonly List<Report> _reports = [];

    private Ticket()
    {
    }

    private Ticket(
        Guid id,
        string title,
        Guid sourceId,
        Guid repositoryId,
        Guid originTriggerId,
        Guid originEvalId,
        Guid originThreadId,
        Guid? originChannelId,
        WorkKind workKind,
        Mandate mandate)
    {
        Id = id;
        Title = title;
        SourceId = sourceId;
        RepositoryId = repositoryId;
        OriginTriggerId = originTriggerId;
        OriginEvalId = originEvalId;
        OriginThreadId = originThreadId;
        OriginChannelId = originChannelId;
        WorkKind = workKind;
        Mandate = mandate;
        Status = TicketStatus.Open;
        Stage = TicketStage.WorkingQueued;
        Outcome = TicketOutcome.None;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public string Title { get; private set; } = string.Empty;
    public Guid SourceId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public Guid OriginTriggerId { get; private set; }
    public Guid OriginEvalId { get; private set; }
    public Guid OriginThreadId { get; private set; }
    public Guid? OriginChannelId { get; private set; }
    public WorkKind WorkKind { get; private set; }
    public Mandate Mandate { get; private set; } = null!;
    public TicketStatus Status { get; private set; }
    public TicketOutcome Outcome { get; private set; }
    public TicketStage Stage { get; private set; }
    public bool StoppedByPerson { get; private set; }
    public string? DeliveryError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public IReadOnlyList<Input> Inputs => _inputs;
    public IReadOnlyList<Assignment> Assignments => _assignments;
    public IReadOnlyList<Report> Reports => _reports;

    public Report? LatestReport => _reports.OrderByDescending(report => report.CreatedAt).FirstOrDefault();
    public Assignment? ActiveAssignment => _assignments.FirstOrDefault(assignment => assignment.Status == AssignmentStatus.Running);

    public static Ticket Accept(
        string title,
        Guid sourceId,
        Guid repositoryId,
        Guid originTriggerId,
        Guid originEvalId,
        Guid originThreadId,
        Guid? originChannelId,
        WorkKind workKind,
        Mandate mandate)
    {
        if (!mandate.IsActive)
        {
            throw new DomainException("Mandate 已失效，不能创建 Ticket。");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "未命名工单";
        }

        var ticket = new Ticket(
            Guid.NewGuid(),
            title.Trim(),
            sourceId,
            repositoryId,
            originTriggerId,
            originEvalId,
            originThreadId,
            originChannelId,
            workKind,
            mandate);
        ticket.Raise(new Events.TicketAccepted(ticket.Id, originThreadId, originEvalId, mandate.PolicyVersion, ticket.CreatedAt));
        return ticket;
    }

    public Input AddInput(InputKind kind, string content, Guid? triggerId = null, bool resumeEnded = false)
    {
        if (Status == TicketStatus.Ended)
        {
            if (StoppedByPerson)
            {
                throw new DomainException("被人停止的 Ticket 不再接收输入。");
            }

            if (!resumeEnded)
            {
                throw new DomainException("已结束的 Ticket 只能通过可恢复的人工跟进重新打开。");
            }

            Resume();
        }

        var input = new Input(Id, kind, content, triggerId);
        _inputs.Add(input);
        Stage = ActiveAssignment is null ? TicketStage.WorkingQueued : TicketStage.Working;
        UpdatedAt = DateTimeOffset.UtcNow;
        return input;
    }

    public Input AddContextInput(InputKind kind, string content, Guid? triggerId = null)
    {
        if (Status == TicketStatus.Ended)
        {
            throw new DomainException("已结束的 Ticket 不再接收上下文 Input。");
        }

        var input = new Input(Id, kind, content, triggerId);
        _inputs.Add(input);
        UpdatedAt = DateTimeOffset.UtcNow;
        return input;
    }

    public void Resume()
    {
        if (Status != TicketStatus.Ended)
        {
            return;
        }

        if (StoppedByPerson)
        {
            throw new DomainException("被人停止的 Ticket 不能恢复。");
        }

        if (!Mandate.IsActive)
        {
            throw new DomainException("Mandate 已失效，不能恢复。");
        }

        Status = TicketStatus.Open;
        Outcome = TicketOutcome.None;
        EndedAt = null;
        Stage = TicketStage.WorkingQueued;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Assignment StartAssignment(string executorName)
    {
        if (Status == TicketStatus.Ended)
        {
            throw new DomainException("已结束的 Ticket 不能启动 Assignment。");
        }

        if (ActiveAssignment is not null)
        {
            throw new DomainException("Ticket 已有运行中的 Assignment。");
        }

        var assignment = new Assignment(Id, executorName);
        _assignments.Add(assignment);
        Stage = TicketStage.WorkingQueued;
        DeliveryError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
        return assignment;
    }

    public void BeginAssignment(Guid assignmentId)
    {
        var assignment = _assignments.FirstOrDefault(item => item.Id == assignmentId)
            ?? throw new DomainException("Assignment 不存在。");
        assignment.Begin();
        Stage = TicketStage.Working;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordReport(Report report)
    {
        if (Status == TicketStatus.Ended)
        {
            throw new DomainException("已结束的 Ticket 不能接收 Report。");
        }

        var assignment = _assignments.FirstOrDefault(item => item.Id == report.AssignmentId)
            ?? throw new DomainException("Report 对应的 Assignment 不存在。");
        assignment.End();
        _reports.Add(report);
        UpdatedAt = DateTimeOffset.UtcNow;
        Raise(new Events.ReportRecorded(Id, assignment.Id, report.Kind.ToString(), UpdatedAt));

        switch (report.Kind)
        {
            case ReportKind.Ready:
                if (string.IsNullOrWhiteSpace(report.PullRequestUrl))
                {
                    End(TicketOutcome.Completed);
                }
                else
                {
                    Stage = TicketStage.PersonReview;
                }

                break;
            case ReportKind.Waiting:
                Stage = TicketStage.Idle;
                break;
            case ReportKind.Blocked:
                Stage = TicketStage.PersonBlocked;
                break;
            case ReportKind.Failed:
                Stage = TicketStage.PersonFailed;
                break;
            case ReportKind.Dropped:
                End(TicketOutcome.Dropped);
                break;
            default:
                Stage = TicketStage.Working;
                break;
        }
    }

    /// <summary>The ticket produced an answer that no reply address can still carry.</summary>
    public void MarkReplyUndeliverable(string error)
    {
        DeliveryError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkInputDelivered(Guid inputId)
    {
        var input = _inputs.FirstOrDefault(item => item.Id == inputId);
        input?.MarkDelivered();
        DeliveryError = null;
        if (Status == TicketStatus.Open && Stage == TicketStage.PersonDelivery)
        {
            Stage = TicketStage.WorkingQueued;
        }

        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkInputDeliveryFailed(Guid inputId, string error)
    {
        var input = _inputs.FirstOrDefault(item => item.Id == inputId);
        input?.MarkDeliveryFailed(error);
        DeliveryError = error;
        if (Status == TicketStatus.Open)
        {
            Stage = TicketStage.PersonDelivery;
        }

        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void StopByPerson(string reason = "person stop")
    {
        Mandate.WithdrawByPerson();
        StoppedByPerson = true;
        End(TicketOutcome.Stopped);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RevokeMandate(string reason)
    {
        Mandate.Revoke(reason);
        End(TicketOutcome.Failed);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool RecoverAssignmentAfterRestart(Guid assignmentId)
    {
        var assignment = _assignments.FirstOrDefault(item => item.Id == assignmentId);
        if (assignment is null || assignment.Status != AssignmentStatus.Running)
        {
            return false;
        }

        assignment.RecoverAfterRestart();
        if (Status == TicketStatus.Open)
        {
            Stage = TicketStage.WorkingQueued;
        }

        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public void End(TicketOutcome outcome)
    {
        if (Status == TicketStatus.Ended)
        {
            return;
        }

        Status = TicketStatus.Ended;
        Stage = TicketStage.Ended;
        Outcome = outcome;
        EndedAt = DateTimeOffset.UtcNow;
        UpdatedAt = EndedAt.Value;
        foreach (var assignment in _assignments.Where(assignment => assignment.Status != AssignmentStatus.Ended))
        {
            assignment.End();
        }

        Raise(new Events.TicketEnded(Id, outcome.ToString(), StoppedByPerson, EndedAt.Value));
    }

}
