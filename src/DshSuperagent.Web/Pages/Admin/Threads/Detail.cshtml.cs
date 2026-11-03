using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Web.Pages.Admin.Threads;

public sealed class DetailModel(
    AppDbContext db,
    IPolicyRepository policies,
    IWorkQueue queue) : PageModel
{
    public Thread Thread { get; private set; } = null!;
    public Source Source { get; private set; } = null!;
    public IReadOnlyList<Trigger> Triggers { get; private set; } = [];
    public IReadOnlyList<Eval> Evals { get; private set; } = [];
    public IReadOnlyList<SubscriptionRow> Subscriptions { get; private set; } = [];
    public IReadOnlyList<ChannelMessage> Messages { get; private set; } = [];
    public IReadOnlyDictionary<Guid, Disposition> Dispositions { get; private set; } = new Dictionary<Guid, Disposition>();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var thread = await db.Threads.AsNoTracking()
            .Include(item => item.Subscriptions)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (thread is null)
        {
            return RedirectToPage("Index");
        }

        Thread = thread;
        Source = await db.Sources.AsNoTracking().FirstAsync(source => source.Id == thread.SourceId, cancellationToken);
        Triggers = await db.Triggers.AsNoTracking()
            .Where(trigger => trigger.ThreadId == id)
            .OrderByDescending(trigger => trigger.ReceivedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        Evals = await db.Evals.AsNoTracking()
            .Where(eval => eval.ThreadId == id)
            .OrderByDescending(eval => eval.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);
        var triggerIds = Triggers.Select(trigger => trigger.Id).ToList();
        Dispositions = await db.Dispositions.AsNoTracking()
            .Where(disposition => triggerIds.Contains(disposition.TriggerId))
            .ToDictionaryAsync(disposition => disposition.TriggerId, cancellationToken);
        var ticketIds = thread.Subscriptions.Select(subscription => subscription.TicketId).ToList();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(ticket => ticketIds.Contains(ticket.Id))
            .ToDictionaryAsync(ticket => ticket.Id, ticket => ticket, cancellationToken);
        Subscriptions = thread.Subscriptions.Select(subscription =>
        {
            tickets.TryGetValue(subscription.TicketId, out var ticket);
            return new SubscriptionRow(
                subscription.TicketId,
                ticket?.Title ?? "（工单已删除）",
                ticket?.Status.ToString() ?? "—",
                ticket?.Stage.ToString() ?? "—",
                subscription.ContextOnly,
                subscription.CreatedAt,
                subscription.DroppedAt);
        }).ToList();
        var channelIds = await db.Channels.AsNoTracking()
            .Where(channel => channel.ThreadId == id)
            .Select(channel => channel.Id)
            .ToListAsync(cancellationToken);
        Messages = await db.ChannelMessages.AsNoTracking()
            .Where(message => channelIds.Contains(message.ChannelId))
            .OrderByDescending(message => message.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostReevaluateAsync(Guid id, CancellationToken cancellationToken)
    {
        var thread = await db.Threads.Include(item => item.Subscriptions)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (thread is null)
        {
            return RedirectToPage("Index");
        }

        if (thread.ActiveEvalId is not null)
        {
            TempData["Error"] = "这个 Thread 已有运行中的 Eval，不能重复启动。";
            return RedirectToPage("Detail", new { id });
        }

        var trigger = await db.Triggers.AsNoTracking()
            .Where(item => item.ThreadId == id)
            .OrderByDescending(item => item.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (trigger is null)
        {
            TempData["Error"] = "Thread 还没有 Trigger。";
            return RedirectToPage("Detail", new { id });
        }

        if (trigger.Strength == TriggerStrength.NotAdmitted)
        {
            TempData["Error"] = "最新 Trigger 在 Gate 被拒绝，不能直接重新评估；需要新的准入请求。";
            return RedirectToPage("Detail", new { id });
        }

        var policy = await policies.GetCurrentAsync(cancellationToken);
        var eval = Eval.Start(thread.Id, thread.SourceId, policy.Version, [trigger.Id]);
        thread.SetActiveEval(eval.Id);
        await db.Evals.AddAsync(eval, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, eval.Id), cancellationToken);
        TempData["Success"] = "已重新排入 Eval。";
        return RedirectToPage("Detail", new { id });
    }

    public sealed record SubscriptionRow(
        Guid TicketId,
        string Title,
        string Status,
        string Stage,
        bool ContextOnly,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DroppedAt);
}
