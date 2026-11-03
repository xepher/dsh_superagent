using DshSuperagent.Core.Storage;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Repositories;

public sealed class IndexModel(AppDbContext db, IWorkQueue queue) : PageModel
{
    public IReadOnlyList<RepositoryRow> Repositories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var ticketCounts = await db.Tickets.AsNoTracking()
            .GroupBy(ticket => ticket.RepositoryId)
            .Select(group => new { RepositoryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.RepositoryId, item => item.Count, cancellationToken);
        var entities = await db.Repositories.AsNoTracking()
            .OrderBy(repository => repository.DisplayName)
            .ToListAsync(cancellationToken);
        Repositories = entities.Select(entity =>
        {
            var pathExists = Directory.Exists(entity.LocalPath);
            var isGit = pathExists && Directory.Exists(Path.Combine(entity.LocalPath, ".git"));
            return new RepositoryRow(
                entity.Id,
                entity.Slug,
                entity.DisplayName,
                entity.LocalPath,
                pathExists,
                isGit,
                entity.Enabled,
                entity.AutoReviewEnabled,
                entity.AllowAutonomousFix,
                entity.PullRequestOwner is not null && entity.PullRequestRepository is not null
                    ? $"{entity.PullRequestOwner}/{entity.PullRequestRepository}"
                    : entity.RemoteUrl,
                ticketCounts.TryGetValue(entity.Id, out var count) ? count : 0);
        }).ToList();
    }

    public async Task<IActionResult> OnPostScanAsync(Guid id, CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (repository is null)
        {
            TempData["Error"] = "仓库不存在。";
            return RedirectToPage();
        }

        if (!repository.Enabled)
        {
            TempData["Error"] = "仓库已停用，先启用再扫描。";
            return RedirectToPage();
        }

        await queue.EnqueueAsync(new WorkItem(WorkItemKind.Scan, id), cancellationToken);
        TempData["Success"] = $"已排入扫描：{repository.DisplayName}。发现的问题会作为 Trigger 进入 Eval。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (repository is null)
        {
            return RedirectToPage();
        }

        if (await db.Tickets.AnyAsync(ticket => ticket.RepositoryId == id, cancellationToken))
        {
            TempData["Error"] = "已有工单引用此仓库，不能删除；请停用。";
            return RedirectToPage();
        }

        db.Repositories.Remove(repository);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "代码仓库已删除。";
        return RedirectToPage();
    }

    public sealed record RepositoryRow(
        Guid Id,
        string Slug,
        string DisplayName,
        string LocalPath,
        bool PathExists,
        bool IsGit,
        bool Enabled,
        bool AutoReviewEnabled,
        bool AllowAutonomousFix,
        string? PullRequestTarget,
        int TicketCount);
}
