using DshSuperagent.Domain.Authorization;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Evals;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public IReadOnlyList<EvalRow> Evals { get; private set; } = [];
    public int AcceptCount { get; private set; }
    public int IgnoreCount { get; private set; }
    public int WouldAcceptCount { get; private set; }
    public int UnavailableCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var threads = await db.Threads.AsNoTracking()
            .ToDictionaryAsync(thread => thread.Id, thread => thread.Title, cancellationToken);
        var evals = await db.Evals.AsNoTracking()
            .OrderByDescending(eval => eval.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        Evals = evals.Select(eval => new EvalRow(
            eval.Id,
            eval.ThreadId,
            threads.TryGetValue(eval.ThreadId, out var title) ? title : eval.ThreadId.ToString("N")[..8],
            eval.Status,
            eval.Decision?.Kind,
            eval.Decision?.Reason ?? eval.Error,
            eval.Decision?.WouldAccept ?? false,
            eval.PolicyVersion,
            eval.CreatedAt)).ToList();
        AcceptCount = evals.Count(eval => eval.Decision?.Kind == DecisionKind.Accept);
        IgnoreCount = evals.Count(eval => eval.Decision?.Kind == DecisionKind.Ignore && !(eval.Decision?.WouldAccept ?? false));
        WouldAcceptCount = evals.Count(eval => eval.Decision?.WouldAccept == true);
        UnavailableCount = evals.Count(eval => eval.Status == EvalStatus.Unavailable);
    }

    public sealed record EvalRow(
        Guid Id,
        Guid ThreadId,
        string ThreadTitle,
        EvalStatus Status,
        DecisionKind? Decision,
        string? Reason,
        bool WouldAccept,
        int PolicyVersion,
        DateTimeOffset CreatedAt);
}
