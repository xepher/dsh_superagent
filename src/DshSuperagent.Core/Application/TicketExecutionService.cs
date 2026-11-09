using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Core.Application;

/// <summary>
/// The application port around the Executor. It records a Report, updates the Ticket stage, and
/// stores the outgoing Channel message before it is sent. The Executor never writes to a Source.
/// </summary>
public sealed class TicketExecutionService(
    ITicketRepository tickets,
    ISourceRepository sources,
    IThreadRepository threads,
    IConfigurationStore configuration,
    IPolicyRepository policies,
    IChannelRepository channels,
    IEnumerable<ITicketExecutor> executors,
    IWorkQueue queue,
    IDomainEventSink domainEvents,
    TicketReplyChannelSelector replyChannels,
    IUnitOfWork unitOfWork)
{
    public async Task ProcessAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.GetAsync(ticketId, cancellationToken);
        if (ticket is null || ticket.Status == TicketStatus.Ended)
        {
            return;
        }

        var assignment = ticket.Assignments
            .Where(item => item.Status == AssignmentStatus.Pending)
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefault();
        if (assignment is null || ticket.ActiveAssignment is not null)
        {
            return;
        }

        var source = await sources.GetAsync(ticket.SourceId, cancellationToken);
        var repository = await configuration.GetRepositoryAsync(ticket.RepositoryId, cancellationToken);
        var policy = await policies.GetCurrentAsync(cancellationToken);
        var prompts = await configuration.GetTeamPromptsAsync(cancellationToken);
        var originTrigger = await threads.GetTriggerAsync(ticket.OriginTriggerId, cancellationToken);

        ticket.BeginAssignment(assignment.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var inputsToRead = ticket.Inputs.Where(input => input.ReadAt is null).ToList();
        var touched = new List<AggregateRoot> { ticket };
        ExecutorReport result;
        if (source is null || repository is null)
        {
            result = new ExecutorReport(
                ReportKind.Failed,
                source is null ? "Source 已被删除，无法执行工单。" : "代码仓库已被删除，无法执行工单。");
        }
        else
        {
            var executor = executors.FirstOrDefault(item => item.Kind == ticket.WorkKind);
            if (executor is null)
            {
                result = new ExecutorReport(ReportKind.Failed, $"没有注册 WorkKind={ticket.WorkKind} 的 Executor。");
            }
            else
            {
                try
                {
                    result = await executor.ExecuteAsync(
                        new TicketExecutionContext(
                            ticket,
                            ticket.Mandate,
                            source,
                            repository,
                            policy,
                            originTrigger,
                            ticket.Inputs.ToList(),
                            prompts),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new ExecutorReport(ReportKind.Failed, $"Executor 执行失败：{ex.Message}");
                }
            }
        }

        foreach (var input in inputsToRead)
        {
            input.MarkRead();
        }

        var report = new Report(
            ticket.Id,
            assignment.Id,
            result.Kind,
            result.Summary,
            result.HumanAction,
            result.PullRequestUrl,
            result.ArtifactJson);
        ticket.RecordReport(report);

        var startAnotherAssignment = false;
        if (ticket.Status == TicketStatus.Open
            && await tickets.HasUnreadInputsAsync(
                ticket.Id,
                inputsToRead.Select(input => input.Id).ToList(),
                cancellationToken)
            && !await tickets.HasQueuedAssignmentAsync(ticket.Id, assignment.Id, cancellationToken)
            && ticket.ActiveAssignment is null)
        {
            ticket.StartAssignment(ticket.WorkKind == WorkKind.Fix ? "repository-fix" : "repository-question");
            startAnotherAssignment = true;
        }

        var content = result.ChannelContent ?? result.Summary;
        if (!string.IsNullOrWhiteSpace(content))
        {
            var channel = await replyChannels.ResolveAsync(ticket, inputsToRead, cancellationToken);
            if (channel is null)
            {
                // A ticket without any chat address (a repository scan, for example) sends nothing by
                // design; only a real conversation that can no longer be reached is a delivery failure.
                var origin = ticket.OriginChannelId is { } originId
                    ? await channels.GetAsync(originId, cancellationToken)
                    : null;
                if (origin is not null && origin.Purpose != ChannelPurpose.None)
                {
                    ticket.MarkReplyUndeliverable("没有可用的应答地址：提问对应的地址都已被使用，回答无法送达。");
                }
            }
            else
            {
                var purpose = result.Kind switch
                {
                    ReportKind.Failed or ReportKind.Blocked => MessagePurpose.Error,
                    ReportKind.Ready when ticket.WorkKind == WorkKind.Fix => MessagePurpose.Result,
                    _ => MessagePurpose.Answer,
                };
                await channels.AddMessageAsync(
                    new ChannelMessage(
                        channel.Id,
                        ticket.Id,
                        purpose,
                        content),
                    cancellationToken);
            }
        }

        await domainEvents.DispatchAsync(touched, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (startAnotherAssignment)
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, ticket.Id), cancellationToken);
        }
    }
}
