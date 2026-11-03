using DshSuperagent.Core.Configuration;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Core.Application;

/// <summary>Everything an Executor may read. The Executor runs outside the domain and reports back through a Report.</summary>
public sealed record TicketExecutionContext(
    Ticket Ticket,
    Mandate Mandate,
    Source Source,
    RepositoryProfile Repository,
    Policy Policy,
    Trigger? OriginTrigger,
    IReadOnlyList<Input> Inputs,
    TeamPrompts Prompts);

public sealed record ExecutorReport(
    ReportKind Kind,
    string Summary,
    string? HumanAction = null,
    string? PullRequestUrl = null,
    string? ChannelContent = null,
    string? RawJson = null,
    string? ArtifactJson = null);

/// <summary>The port that runs one Assignment. Implementations are infrastructure/adapters, not domain.</summary>
public interface ITicketExecutor
{
    WorkKind Kind { get; }
    string Name { get; }
    Task<ExecutorReport> ExecuteAsync(TicketExecutionContext context, CancellationToken cancellationToken);
}

public sealed record AssessmentRequest(
    Trigger Trigger,
    Thread Thread,
    Policy Policy,
    RepositoryProfile? Repository);

public sealed record AssessmentResult(
    bool Accept,
    string Reason,
    IReadOnlyList<Guid> MatchedRuleIds,
    string Scope,
    string? RawJson = null);

/// <summary>
/// The model layer of Eval. It judges every enabled rule once. A deterministic fallback keeps
/// explicit local development usable when no model is configured, but never turns a missing model
/// into an inferred accept.
/// </summary>
public interface IPolicyAssessor
{
    Task<AssessmentResult> AssessAsync(AssessmentRequest request, CancellationToken cancellationToken);
}
