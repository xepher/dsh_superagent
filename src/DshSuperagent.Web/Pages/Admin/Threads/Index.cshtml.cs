using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Threads;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public IReadOnlyList<ThreadRow> Threads { get; private set; } = [];
    public string View { get; private set; } = "all";

    public async Task OnGetAsync(string? view, CancellationToken cancellationToken)
    {
        View = view is "subscribed" or "evaluating" or "new" ? view : "all";
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var sources = await db.Sources.AsNoTracking()
            .ToDictionaryAsync(source => source.Id, source => source.Name, cancellationToken);
        var threads = await db.Threads.AsNoTracking()
            .Include(thread => thread.Subscriptions)
            .OrderByDescending(thread => thread.LastActivityAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        var triggerCounts = await db.Triggers.AsNoTracking()
            .GroupBy(trigger => trigger.ThreadId)
            .Select(group => new { ThreadId = group.Key, Count = group.Count(), New = group.Count(trigger => trigger.ReceivedAt >= since) })
            .ToDictionaryAsync(item => item.ThreadId, item => item, cancellationToken);
        var evalIds = threads.Where(thread => thread.ActiveEvalId is not null)
            .Select(thread => thread.ActiveEvalId!.Value)
            .ToList();
        var evals = await db.Evals.AsNoTracking()
            .Where(eval => evalIds.Contains(eval.Id))
            .ToDictionaryAsync(eval => eval.Id, eval => eval.Status, cancellationToken);

        Threads = threads
            .Select(thread =>
            {
                triggerCounts.TryGetValue(thread.Id, out var count);
                var live = thread.Subscriptions.Where(subscription => subscription.DroppedAt is null).ToList();
                return new ThreadRow(
                    thread.Id,
                    thread.Title,
                    sources.TryGetValue(thread.SourceId, out var sourceName) ? sourceName : thread.SourceId.ToString("N")[..8],
                    thread.LastActivityAt,
                    count?.Count ?? 0,
                    count?.New ?? 0,
                    live.Count,
                    live.Count(subscription => subscription.ContextOnly),
                    thread.ActiveEvalId,
                    thread.ActiveEvalId is { } evalId && evals.TryGetValue(evalId, out var status) ? status.ToString() : null,
                    thread.PendingTriggerIds.Count);
            })
            .Where(thread => View switch
            {
                "subscribed" => thread.LiveSubscriptions > 0,
                "evaluating" => thread.ActiveEvalId is not null || thread.PendingTriggers > 0,
                "new" => thread.NewTriggers24h > 0,
                _ => true,
            })
            .ToList();
    }

    public sealed record ThreadRow(
        Guid Id,
        string Title,
        string SourceName,
        DateTimeOffset LastActivityAt,
        int Triggers,
        int NewTriggers24h,
        int LiveSubscriptions,
        int ContextOnlySubscriptions,
        Guid? ActiveEvalId,
        string? ActiveEvalStatus,
        int PendingTriggers);
}
