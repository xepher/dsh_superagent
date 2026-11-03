using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Tickets;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public IReadOnlyList<TicketRow> Tickets { get; private set; } = [];
    public string View { get; private set; } = "open";
    public int NeedsPerson { get; private set; }
    public int OpenCount { get; private set; }
    public int EndedCount { get; private set; }

    public async Task OnGetAsync(string? view, CancellationToken cancellationToken)
    {
        View = view is "person" or "open" or "ended" or "all" ? view : "open";
        var repositories = await db.Repositories.AsNoTracking()
            .ToDictionaryAsync(repository => repository.Id, repository => repository.DisplayName, cancellationToken);
        var sources = await db.Sources.AsNoTracking()
            .ToDictionaryAsync(source => source.Id, source => source.Name, cancellationToken);
        var tickets = await db.Tickets.AsNoTracking()
            .Include(ticket => ticket.Reports)
            .OrderByDescending(ticket => ticket.UpdatedAt)
            .Take(300)
            .ToListAsync(cancellationToken);
        NeedsPerson = tickets.Count(ticket => IsPersonStage(ticket.Stage));
        OpenCount = tickets.Count(ticket => ticket.Status == TicketStatus.Open);
        EndedCount = tickets.Count(ticket => ticket.Status == TicketStatus.Ended);
        Tickets = tickets
            .Where(ticket => View switch
            {
                "person" => IsPersonStage(ticket.Stage),
                "open" => ticket.Status == TicketStatus.Open,
                "ended" => ticket.Status == TicketStatus.Ended,
                _ => true,
            })
            .Select(ticket =>
            {
                var latest = ticket.LatestReport;
                return new TicketRow(
                    ticket.Id,
                    ticket.Title,
                    ticket.WorkKind,
                    ticket.Status,
                    ticket.Stage,
                    repositories.TryGetValue(ticket.RepositoryId, out var repository) ? repository : ticket.RepositoryId.ToString("N")[..8],
                    sources.TryGetValue(ticket.SourceId, out var source) ? source : "—",
                    latest?.Kind,
                    latest?.PullRequestUrl,
                    ticket.UpdatedAt,
                    ticket.StoppedByPerson);
            })
            .ToList();
    }

    private static bool IsPersonStage(TicketStage stage) =>
        stage is TicketStage.PersonDelivery or TicketStage.PersonAnswer or TicketStage.PersonReview
            or TicketStage.PersonBlocked or TicketStage.PersonFailed;

    public sealed record TicketRow(
        Guid Id,
        string Title,
        WorkKind WorkKind,
        TicketStatus Status,
        TicketStage Stage,
        string Repository,
        string Source,
        ReportKind? LatestReport,
        string? PullRequestUrl,
        DateTimeOffset UpdatedAt,
        bool StoppedByPerson);
}
