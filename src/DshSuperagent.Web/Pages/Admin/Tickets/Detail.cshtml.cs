using System.Text;
using System.Text.Json;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Tickets;

public sealed class DetailModel(AppDbContext db, IWorkQueue queue) : PageModel
{
    public Ticket Ticket { get; private set; } = null!;
    public Source Source { get; private set; } = null!;
    public Domain.Sources.Policy Policy { get; private set; } = null!;
    public RepositoryEntity Repository { get; private set; } = null!;
    public Domain.Threads.Trigger? OriginTrigger { get; private set; }
    public Channel? Channel { get; private set; }
    public IReadOnlyList<ChannelMessage> Messages { get; private set; } = [];
    public IReadOnlyList<Domain.Threads.Subscription> Subscriptions { get; private set; } = [];
    public string? Patch { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return RedirectToPage("Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostStopAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(item => item.Inputs).Include(item => item.Assignments).Include(item => item.Reports)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return RedirectToPage("Index");
        }

        if (ticket.Status == TicketStatus.Open)
        {
            ticket.StopByPerson("manual stop from panel");
            if (ticket.OriginChannelId is { } channelId)
            {
                await db.ChannelMessages.AddAsync(
                    new ChannelMessage(channelId, ticket.Id, MessagePurpose.Notice, "管理员已从面板停止这个工单。"),
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = "工单已停止；Mandate 被撤回，后续消息不会再恢复它。";
        }

        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(item => item.Inputs).Include(item => item.Assignments).Include(item => item.Reports)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return RedirectToPage("Index");
        }

        if (ticket.Status == TicketStatus.Ended)
        {
            TempData["Error"] = "已结束的工单不能重试；需要人工跟进重新打开。";
            return RedirectToPage("Detail", new { id });
        }

        if (ticket.ActiveAssignment is not null)
        {
            TempData["Error"] = "工单已有运行中的 Assignment。";
            return RedirectToPage("Detail", new { id });
        }

        ticket.AddInput(InputKind.Retry, "管理员要求重试。");
        ticket.StartAssignment(ticket.WorkKind == WorkKind.Fix ? "repository-fix" : "repository-question");
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, ticket.Id), cancellationToken);
        TempData["Success"] = "已创建新的 Assignment。";
        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(item => item.Inputs).Include(item => item.Assignments).Include(item => item.Reports)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null || ticket.Status == TicketStatus.Ended || ticket.ActiveAssignment is not null)
        {
            return RedirectToPage("Detail", new { id });
        }

        if (ticket.Assignments.All(assignment => assignment.Status == AssignmentStatus.Ended))
        {
            ticket.StartAssignment(ticket.WorkKind == WorkKind.Fix ? "repository-fix" : "repository-question");
            await db.SaveChangesAsync(cancellationToken);
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, ticket.Id), cancellationToken);
            TempData["Success"] = "已排入执行队列。";
        }

        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnGetPatchAsync(Guid id, Guid reportId, CancellationToken cancellationToken)
    {
        var report = await db.Reports.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == reportId && item.TicketId == id, cancellationToken);
        if (report?.ArtifactJson is null)
        {
            return NotFound();
        }

        try
        {
            using var document = JsonDocument.Parse(report.ArtifactJson);
            var patch = document.RootElement.TryGetProperty("patch", out var patchElement) ? patchElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(patch))
            {
                return NotFound();
            }

            return File(Encoding.UTF8.GetBytes(patch), "text/x-patch", $"{id:N}-{reportId:N}.patch");
        }
        catch (JsonException)
        {
            return NotFound();
        }
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Include(item => item.Inputs)
            .Include(item => item.Assignments)
            .Include(item => item.Reports)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            TempData["Error"] = "工单不存在。";
            return false;
        }

        Ticket = ticket;
        Source = await db.Sources.AsNoTracking().FirstAsync(source => source.Id == ticket.SourceId, cancellationToken);
        Policy = await db.Policies.AsNoTracking()
            .Include(policy => policy.Rules)
            .OrderByDescending(policy => policy.Version)
            .FirstAsync(cancellationToken);
        Repository = await db.Repositories.AsNoTracking().FirstAsync(repository => repository.Id == ticket.RepositoryId, cancellationToken);
        OriginTrigger = await db.Triggers.AsNoTracking().FirstOrDefaultAsync(trigger => trigger.Id == ticket.OriginTriggerId, cancellationToken);
        Channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(channel => channel.Id == ticket.OriginChannelId, cancellationToken);
        Messages = await db.ChannelMessages.AsNoTracking()
            .Where(message => message.TicketId == ticket.Id)
            .OrderByDescending(message => message.CreatedAt)
            .ToListAsync(cancellationToken);
        Subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.TicketId == ticket.Id)
            .ToListAsync(cancellationToken);
        var latest = ticket.Reports.OrderByDescending(report => report.CreatedAt).FirstOrDefault();
        if (latest?.ArtifactJson is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(latest.ArtifactJson);
                Patch = document.RootElement.TryGetProperty("patch", out var patchElement) ? patchElement.GetString() : null;
            }
            catch (JsonException)
            {
                Patch = null;
            }
        }

        return true;
    }
}
