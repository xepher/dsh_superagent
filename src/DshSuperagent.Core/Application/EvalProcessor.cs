using System.Text.Json;
using DshSuperagent.Core.Commands;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Core.Application;

/// <summary>
/// Eval is the only place that can create a Ticket. It merges the gate result, the policy
/// assessment and the accept mode into one Decision, then either accepts with a Mandate or records
/// why nothing started.
/// </summary>
public sealed class EvalProcessor(
    IPolicyRepository policies,
    ISourceRepository sources,
    IThreadRepository threads,
    ITicketRepository tickets,
    IChannelRepository channels,
    IConfigurationStore configuration,
    IPolicyAssessor assessor,
    IWorkQueue queue,
    IDomainEventSink domainEvents,
    IUnitOfWork unitOfWork)
{
    public async Task ProcessAsync(Guid evalId, CancellationToken cancellationToken)
    {
        var eval = await threads.GetEvalAsync(evalId, cancellationToken);
        if (eval is null || !eval.IsActive)
        {
            return;
        }

        var thread = await threads.GetAsync(eval.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Eval 关联的 Thread 不存在。");
        var policy = await policies.GetCurrentAsync(cancellationToken);
        if (eval.PolicyVersion != policy.Version)
        {
            eval.RequeueForPolicyChange(policy.Version, "Policy 已更新，Eval 丢弃旧研判并重新排队。");
            await SaveAsync([eval, thread], cancellationToken);
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, eval.Id), cancellationToken);
            return;
        }

        var triggers = await threads.GetTriggersAsync(eval.TriggerIds, cancellationToken);
        if (triggers.Count == 0)
        {
            eval.MarkUnavailable("Eval 没有可读取的 Trigger。");
            thread.ClearActiveEval(eval.Id);
            var next = await StartNextEvalAsync(thread, policy.Version, [eval, thread], cancellationToken);
            await SaveAsync([eval, thread], cancellationToken);
            if (next is { } pendingEvalId)
            {
                await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, pendingEvalId), cancellationToken);
            }

            return;
        }

        eval.Begin();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var candidate = triggers.OrderByDescending(trigger => trigger.ReceivedAt).First();
        var touched = new List<AggregateRoot> { eval, thread };
        Decision decision;
        Guid? acceptedTicketId = null;
        Guid? nextEvalId = null;
        try
        {
            var stopTrigger = triggers.LastOrDefault(trigger => BotCommandParser.Parse(trigger.Text).Kind == BotCommandKind.Stop);
            if (stopTrigger is not null)
            {
                decision = await StopAsync(thread, stopTrigger, cancellationToken, touched);
            }
            else
            {
                decision = await DecideAsync(thread, candidate, policy, triggers, cancellationToken);
            }

            eval.Complete(decision);
            thread.ClearActiveEval(eval.Id);
            await RecordDispositionAsync(thread, candidate, decision, eval, cancellationToken);
            if (decision.Kind == DecisionKind.Accept && decision.Scope is not null)
            {
                acceptedTicketId = await AcceptAsync(thread, candidate, decision, policy, eval, touched, cancellationToken);
            }
            else if (decision.Kind == DecisionKind.Ignore)
            {
                await NotifyAsync(
                    candidate,
                    decision.WouldAccept
                        ? "审查候选已记录为「本可接单」，当前 accept mode 为 observe，未创建工单。管理员可在 Policy 中切换到 auto。"
                        : $"未创建工单：{decision.Reason}",
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eval.MarkUnavailable(ex.Message);
            thread.ClearActiveEval(eval.Id);
            await NotifyAsync(candidate, $"评估失败，未创建工单：{ex.Message}", cancellationToken);
        }

        nextEvalId = await StartNextEvalAsync(thread, policy.Version, touched, cancellationToken);
        await SaveAsync(touched, cancellationToken);
        if (acceptedTicketId is { } ticketId)
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, ticketId), cancellationToken);
        }

        if (nextEvalId is { } nextId)
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, nextId), cancellationToken);
        }
    }

    private async Task<Decision> DecideAsync(
        Thread thread,
        Trigger candidate,
        Domain.Sources.Policy policy,
        IReadOnlyList<Trigger> batch,
        CancellationToken cancellationToken)
    {
        var scope = ResolveScope(candidate);
        var repository = await ResolveRepositoryAsync(candidate, cancellationToken);
        if (repository is null)
        {
            return Decision.Ignore(candidate.Strength, "没有可用的代码仓库，不能创建工单。");
        }

        if (candidate.Strength == TriggerStrength.Explicit)
        {
            return Decision.Accept(
                TriggerStrength.Explicit,
                "允许列表内的明确请求。",
                [],
                scope);
        }

        if (policy.AcceptMode == AcceptMode.Off)
        {
            return Decision.Ignore(TriggerStrength.Inferred, "accept mode off 不接纳推断候选。");
        }

        if (IsInfoFinding(candidate.PayloadJson))
        {
            return Decision.Ignore(candidate.Strength, "Info 级静态审查发现只记录，不创建修复工单。");
        }

        if (string.Equals(scope, "fix", StringComparison.OrdinalIgnoreCase)
            && candidate.Actor.Kind != ActorKind.Human
            && !repository.AllowAutonomousFix)
        {
            return Decision.Ignore(candidate.Strength, "仓库未开启「允许自动修复」，推断发现只记录。");
        }

        var assessment = await assessor.AssessAsync(
            new AssessmentRequest(candidate, thread, policy, repository),
            cancellationToken);
        if (!assessment.Accept)
        {
            return Decision.Ignore(
                candidate.Strength,
                assessment.Reason,
                false,
                assessment.RawJson);
        }

        if (policy.AcceptMode == AcceptMode.Observe)
        {
            return Decision.Ignore(
                candidate.Strength,
                $"{assessment.Reason}（observe 模式只记录，不接单）",
                true,
                assessment.RawJson);
        }

        var acceptedScope = string.IsNullOrWhiteSpace(assessment.Scope) ? scope : assessment.Scope;
        return Decision.Accept(
            candidate.Strength,
            assessment.Reason,
            assessment.MatchedRuleIds,
            acceptedScope,
            assessment.RawJson);
    }

    private async Task<Decision> StopAsync(
        Thread thread,
        Trigger trigger,
        CancellationToken cancellationToken,
        List<AggregateRoot> touched)
    {
        var threadTickets = await tickets.GetTicketsForThreadAsync(thread.Id, cancellationToken);
        var target = threadTickets
            .Where(ticket => ticket.Status == TicketStatus.Open)
            .OrderByDescending(ticket => ticket.CreatedAt)
            .FirstOrDefault();
        if (target is null)
        {
            return Decision.Ignore(TriggerStrength.Explicit, "当前 Thread 没有可停止的开放工单。");
        }

        target.StopByPerson($"person stopped via {trigger.Actor.Id}");
        touched.Add(target);
        await NotifyAsync(trigger, "已按人工请求停止当前工单。", cancellationToken);
        return Decision.Stop("人工明确要求停止工单。");
    }

    private async Task<Guid?> AcceptAsync(
        Thread thread,
        Trigger candidate,
        Decision decision,
        Domain.Sources.Policy policy,
        Eval eval,
        List<AggregateRoot> touched,
        CancellationToken cancellationToken)
    {
        var repository = await ResolveRepositoryAsync(candidate, cancellationToken);
        if (repository is null)
        {
            await NotifyAsync(candidate, "没有可用的代码仓库，无法创建工单。请管理员配置默认仓库。", cancellationToken);
            return null;
        }

        var workKind = string.Equals(decision.Scope, "fix", StringComparison.OrdinalIgnoreCase)
            ? WorkKind.Fix
            : WorkKind.Answer;
        if (workKind == WorkKind.Fix && candidate.Actor.Kind != ActorKind.Human && !repository.AllowAutonomousFix)
        {
            await NotifyAsync(candidate, "仓库未开启「允许自动修复」，本次发现只记录，不创建修复工单。", cancellationToken);
            return null;
        }

        var basis = candidate.Actor.Kind == ActorKind.Human
            ? (candidate.Strength == TriggerStrength.Explicit ? MandateBasis.ExplicitRequest : MandateBasis.AdmissionCell)
            : MandateBasis.BotRule;
        var mandate = Mandate.Grant(
            policy.Version,
            decision.Strength,
            workKind.ToString(),
            decision.MatchedRuleIds,
            candidate.Actor,
            candidate.Addressed,
            candidate.SourceId,
            candidate.ReceiptScope,
            basis);
        var ticket = Ticket.Accept(
            candidate.Title,
            candidate.SourceId,
            repository.Id,
            candidate.Id,
            eval.Id,
            thread.Id,
            candidate.ChannelId,
            workKind,
            mandate);
        if (thread.TrySubscribe(ticket.Id, contextOnly: false))
        {
            var input = ticket.AddInput(InputKind.Trigger, candidate.Text, candidate.Id);
            ticket.MarkInputDelivered(input.Id);
            ticket.StartAssignment(workKind == WorkKind.Fix ? "repository-fix" : "repository-question");
            await tickets.AddAsync(ticket, cancellationToken);
            touched.Add(ticket);
            return ticket.Id;
        }

        return null;
    }

    private async Task<Guid?> StartNextEvalAsync(
        Thread thread,
        int policyVersion,
        List<AggregateRoot> touched,
        CancellationToken cancellationToken)
    {
        var pending = thread.TakePendingTriggers();
        if (pending.Count == 0)
        {
            return null;
        }

        var next = Eval.Start(thread.Id, thread.SourceId, policyVersion, pending);
        thread.SetActiveEval(next.Id);
        await threads.AddEvalAsync(next, cancellationToken);
        touched.Add(next);
        return next.Id;
    }

    private async Task<RepositoryProfile?> ResolveRepositoryAsync(Trigger trigger, CancellationToken cancellationToken)
    {
        var slug = ReadPayload(trigger.PayloadJson, "repository_slug");
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return await configuration.GetRepositoryBySlugAsync(slug, cancellationToken);
        }

        var source = await sources.GetAsync(trigger.SourceId, cancellationToken);
        if (source?.DefaultRepositoryId is { } defaultId)
        {
            var configured = await configuration.GetRepositoryAsync(defaultId, cancellationToken);
            if (configured is { Enabled: true })
            {
                return configured;
            }
        }

        var repositories = await configuration.GetRepositoriesAsync(enabledOnly: true, cancellationToken);
        return repositories.Count == 1 ? repositories[0] : null;
    }

    private async Task NotifyAsync(Trigger trigger, string content, CancellationToken cancellationToken)
    {
        if (trigger.ChannelId is not { } channelId)
        {
            return;
        }

        var channel = await channels.GetAsync(channelId, cancellationToken);
        if (channel is null || channel.Purpose == ChannelPurpose.None)
        {
            return;
        }

        await channels.AddMessageAsync(
            new ChannelMessage(channel.Id, null, MessagePurpose.Notice, content),
            cancellationToken);
    }

    private async Task RecordDispositionAsync(
        Thread thread,
        Trigger trigger,
        Decision decision,
        Eval eval,
        CancellationToken cancellationToken)
    {
        await threads.AddDispositionAsync(
            new Disposition(
                trigger.Id,
                thread.Id,
                decision.Kind switch
                {
                    DecisionKind.Accept => DispositionKind.Admitted,
                    DecisionKind.Stop => DispositionKind.Stopped,
                    _ => DispositionKind.Evaluated,
                },
                decision.Reason,
                eval.Id,
                null),
            cancellationToken);
    }

    private async Task SaveAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken)
    {
        var list = aggregates.Distinct().ToList();
        await domainEvents.DispatchAsync(list, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static string ResolveScope(Trigger trigger)
    {
        var command = ReadPayload(trigger.PayloadJson, "command");
        if (string.Equals(command, "Fix", StringComparison.OrdinalIgnoreCase)
            || ReadPayload(trigger.PayloadJson, "finding_id") is not null)
        {
            return "fix";
        }

        return "answer";
    }

    private static bool IsInfoFinding(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("finding", out var finding)
                   && finding.ValueKind == JsonValueKind.Object
                   && finding.TryGetProperty("severity", out var severity)
                   && string.Equals(severity.GetString(), "Info", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadPayload(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
