using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Data;

/// <summary>
/// Turns in-process domain events into an operator-facing audit trail. This is deliberately a
/// side effect: the domain event is already recorded by the aggregate and never drives routing.
/// </summary>
public sealed class EfDomainEventSink(AppDbContext db) : IDomainEventSink
{
    public async Task DispatchAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken)
    {
        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                var (category, summary, detail) = domainEvent switch
                {
                    TriggerRecorded trigger => ("trigger", "记录 Trigger", $"{trigger.ExternalId} · Thread {trigger.ThreadId}"),
                    EvalCompleted eval => ("eval", $"Eval 完成：{eval.Decision}", $"Policy v{eval.PolicyVersion}"),
                    TicketAccepted ticket => ("ticket", "接受工单", $"Ticket {ticket.TicketId} · Policy v{ticket.PolicyVersion}"),
                    ReportRecorded report => ("report", $"执行报告：{report.Kind}", $"Ticket {report.TicketId}"),
                    TicketEnded ended => ("ticket", $"工单结束：{ended.Outcome}", $"Ticket {ended.TicketId}"),
                    PolicyChanged policy => ("policy", $"Policy 版本更新：v{policy.Version}", $"accept mode: {policy.AcceptMode}"),
                    _ => (null, null, null),
                };
                if (category is null || summary is null)
                {
                    continue;
                }

                await db.AuditEvents.AddAsync(new AuditEventEntity
                {
                    At = domainEvent.OccurredAt,
                    Category = category,
                    Summary = summary,
                    Detail = detail,
                    Actor = "domain",
                }, cancellationToken);
            }

            aggregate.ClearDomainEvents();
        }
    }
}

public sealed class EfStreamReplyStore(IDbContextFactory<AppDbContext> dbFactory) : IStreamReplyStore
{
    public async Task<StreamReplyState?> GetAsync(string streamId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var reply = await db.StreamReplies.AsNoTracking()
            .FirstOrDefaultAsync(item => item.StreamId == streamId, cancellationToken);
        return reply is null ? null : new StreamReplyState(reply.StreamId, reply.Finished, reply.Content);
    }

    public async Task SetRunningAsync(string streamId, string content, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var reply = await db.StreamReplies.FirstOrDefaultAsync(item => item.StreamId == streamId, cancellationToken);
        if (reply is null)
        {
            db.StreamReplies.Add(new StreamReplyEntity
            {
                StreamId = streamId,
                Finished = false,
                Content = content,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else if (!reply.Finished)
        {
            reply.Content = content;
            reply.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(string streamId, string content, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var reply = await db.StreamReplies.FirstOrDefaultAsync(item => item.StreamId == streamId, cancellationToken);
        if (reply is null)
        {
            db.StreamReplies.Add(new StreamReplyEntity
            {
                StreamId = streamId,
                Finished = true,
                Content = content,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            reply.Finished = true;
            reply.Content = content;
            reply.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (Random.Shared.Next(20) == 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-48);
            await db.StreamReplies.Where(item => item.UpdatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
