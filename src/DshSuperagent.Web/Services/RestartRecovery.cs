using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Web.Services;

/// <summary>
/// Recovers work interrupted by a host restart and bounds repeated recovery. A stale Running Eval
/// releases its Thread after three attempts; a stale Running Assignment returns to Pending.
/// </summary>
/// <summary>Restart recovery is bounded and only touches stale Running/Queued state.</summary>
public static class WorkRecovery
{
    public static async Task RecoverAsync(
        IServiceProvider services,
        bool recoverAllRunning,
        CancellationToken cancellationToken)
    {
        var threads = services.GetRequiredService<IThreadRepository>();
        var tickets = services.GetRequiredService<ITicketRepository>();
        var policies = services.GetRequiredService<IPolicyRepository>();
        var queue = services.GetRequiredService<IWorkQueue>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var work = new List<WorkItem>();
        var db = services.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddMinutes(-30);

        foreach (var eval in await threads.GetRunningEvalsAsync(cancellationToken))
        {
            var thread = await threads.GetAsync(eval.ThreadId, cancellationToken);
            if (thread is null)
            {
                continue;
            }

            if (eval.Status == EvalStatus.Running && (recoverAllRunning || eval.StartedAt < staleBefore))
            {
                if (eval.TryRecoverAfterRestart("host restarted or Eval stopped progressing"))
                {
                    if (thread.ActiveEvalId is null)
                    {
                        thread.SetActiveEval(eval.Id);
                    }

                    work.Add(new WorkItem(WorkItemKind.Eval, eval.Id));
                }
                else
                {
                    thread.ClearActiveEval(eval.Id);
                    await StartPendingAsync(thread, policies, threads, work, cancellationToken);
                }
            }
            else if (!recoverAllRunning && eval.Status == EvalStatus.Queued && eval.CreatedAt < staleBefore)
            {
                if (eval.TryRecoverQueued("Eval 长时间没有推进"))
                {
                    work.Add(new WorkItem(WorkItemKind.Eval, eval.Id));
                }
                else
                {
                    thread.ClearActiveEval(eval.Id);
                    await StartPendingAsync(thread, policies, threads, work, cancellationToken);
                }
            }
        }

        var runningAssignments = await db.Assignments.AsNoTracking()
            .Where(assignment => assignment.Status == Domain.Work.AssignmentStatus.Running
                                 && assignment.StartedAt != null)
            .Select(assignment => new { assignment.TicketId, assignment.Id, assignment.StartedAt })
            .ToListAsync(cancellationToken);
        var staleAssignments = runningAssignments
            .Where(assignment => recoverAllRunning || (assignment.StartedAt is { } startedAt && startedAt < staleBefore))
            .ToList();
        foreach (var stale in staleAssignments)
        {
            var ticket = await tickets.GetAsync(stale.TicketId, cancellationToken);
            if (ticket is not null && ticket.RecoverAssignmentAfterRestart(stale.Id))
            {
                work.Add(new WorkItem(WorkItemKind.Assignment, ticket.Id));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var item in work)
        {
            await queue.EnqueueAsync(item, cancellationToken);
        }
    }

    private static async Task StartPendingAsync(
        Thread thread,
        IPolicyRepository policies,
        IThreadRepository threads,
        List<WorkItem> work,
        CancellationToken cancellationToken)
    {
        if (thread.ActiveEvalId is not null)
        {
            return;
        }

        var pending = thread.TakePendingTriggers();
        if (pending.Count == 0)
        {
            return;
        }

        var policy = await policies.GetCurrentAsync(cancellationToken);
        var next = Eval.Start(thread.Id, thread.SourceId, policy.Version, pending);
        thread.SetActiveEval(next.Id);
        await threads.AddEvalAsync(next, cancellationToken);
        work.Add(new WorkItem(WorkItemKind.Eval, next.Id));
    }
}
