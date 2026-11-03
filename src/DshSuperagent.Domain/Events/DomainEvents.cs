using DshSuperagent.Domain.Common;

namespace DshSuperagent.Domain.Events;

public sealed record TriggerRecorded(
    Guid TriggerId,
    Guid SourceId,
    Guid ThreadId,
    string ExternalId,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record EvalCompleted(
    Guid EvalId,
    Guid ThreadId,
    string Decision,
    int PolicyVersion,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record TicketAccepted(
    Guid TicketId,
    Guid ThreadId,
    Guid EvalId,
    int PolicyVersion,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ReportRecorded(
    Guid TicketId,
    Guid AssignmentId,
    string Kind,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record TicketEnded(
    Guid TicketId,
    string Outcome,
    bool StoppedByPerson,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PolicyChanged(
    int Version,
    string AcceptMode,
    DateTimeOffset OccurredAt) : IDomainEvent;
