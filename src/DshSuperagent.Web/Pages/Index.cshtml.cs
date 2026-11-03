using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public int Triggers24h { get; private set; }
    public int Evals24h { get; private set; }
    public int Tickets24h { get; private set; }
    public int RunningAssignments { get; private set; }
    public int OpenTickets { get; private set; }
    public int UnavailableEvals { get; private set; }
    public int FailedMessages { get; private set; }
    public int EnabledSources { get; private set; }
    public int AllowedUsers { get; private set; }
    public int EnabledRepositories { get; private set; }
    public bool HasModel { get; private set; }
    public bool HasPullRequest { get; private set; }
    public bool HasAutoReview { get; private set; }
    public AcceptMode AcceptMode { get; private set; }
    public int PolicyVersion { get; private set; }
    public IReadOnlyList<TicketRow> NeedsPerson { get; private set; } = [];
    public IReadOnlyList<TicketRow> RecentTickets { get; private set; } = [];
    public IReadOnlyList<EvalRow> RecentEvals { get; private set; } = [];
    public IReadOnlyList<AuditRow> RecentAudit { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-24);
        Triggers24h = await db.Triggers.CountAsync(trigger => trigger.ReceivedAt >= since, cancellationToken);
        Evals24h = await db.Evals.CountAsync(eval => eval.CreatedAt >= since, cancellationToken);
        Tickets24h = await db.Tickets.CountAsync(ticket => ticket.CreatedAt >= since, cancellationToken);
        RunningAssignments = await db.Assignments.CountAsync(assignment => assignment.Status == AssignmentStatus.Running, cancellationToken);
        OpenTickets = await db.Tickets.CountAsync(ticket => ticket.Status == TicketStatus.Open, cancellationToken);
        UnavailableEvals = await db.Evals.CountAsync(
            eval => eval.Status == EvalStatus.Unavailable && eval.CompletedAt >= since,
            cancellationToken);
        FailedMessages = await db.ChannelMessages.CountAsync(
            message => message.Status == MessageStatus.Dropped || (message.Status == MessageStatus.Failed && message.Attempts >= 3),
            cancellationToken);

        var policy = await db.Policies.AsNoTracking()
            .Include(item => item.Rules)
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        AcceptMode = policy?.AcceptMode ?? AcceptMode.Observe;
        PolicyVersion = policy?.Version ?? 0;
        EnabledSources = await db.Sources.CountAsync(source => source.Enabled, cancellationToken);
        AllowedUsers = policy?.SourcePolicies.Sum(part => part.AllowedUsers.Count) ?? 0;
        EnabledRepositories = await db.Repositories.CountAsync(repository => repository.Enabled, cancellationToken);
        HasModel = await db.LlmProfiles.AnyAsync(profile => profile.Enabled, cancellationToken);
        HasPullRequest = await db.PullRequestProfiles.AnyAsync(profile => profile.Enabled, cancellationToken);
        HasAutoReview = await db.Repositories.AnyAsync(repository => repository.Enabled && repository.AutoReviewEnabled, cancellationToken);

        var personStages = new[]
        {
            TicketStage.PersonDelivery,
            TicketStage.PersonAnswer,
            TicketStage.PersonReview,
            TicketStage.PersonBlocked,
            TicketStage.PersonFailed,
        };
        NeedsPerson = await db.Tickets.AsNoTracking()
            .Where(ticket => ticket.Status == TicketStatus.Open && personStages.Contains(ticket.Stage))
            .OrderBy(ticket => ticket.UpdatedAt)
            .Take(8)
            .Select(ticket => new TicketRow(
                ticket.Id,
                ticket.Title,
                ticket.Stage.ToString(),
                ticket.WorkKind.ToString(),
                ticket.UpdatedAt,
                ticket.RepositoryId))
            .ToListAsync(cancellationToken);

        RecentTickets = await db.Tickets.AsNoTracking()
            .OrderByDescending(ticket => ticket.CreatedAt)
            .Take(6)
            .Select(ticket => new TicketRow(
                ticket.Id,
                ticket.Title,
                ticket.Stage.ToString(),
                ticket.WorkKind.ToString(),
                ticket.CreatedAt,
                ticket.RepositoryId))
            .ToListAsync(cancellationToken);

        RecentEvals = await db.Evals.AsNoTracking()
            .OrderByDescending(eval => eval.CreatedAt)
            .Take(5)
            .Select(eval => new EvalRow(
                eval.Id,
                eval.ThreadId,
                eval.Status.ToString(),
                eval.Decision == null ? "—" : eval.Decision.Kind.ToString(),
                eval.Decision == null ? null : eval.Decision.Reason,
                eval.PolicyVersion,
                eval.CreatedAt))
            .ToListAsync(cancellationToken);

        RecentAudit = await db.AuditEvents.AsNoTracking()
            .OrderByDescending(audit => audit.At)
            .Take(6)
            .Select(audit => new AuditRow(audit.At, audit.Category, audit.Summary))
            .ToListAsync(cancellationToken);
    }

    public sealed record TicketRow(Guid Id, string Title, string Stage, string WorkKind, DateTimeOffset At, Guid RepositoryId);
    public sealed record EvalRow(Guid Id, Guid ThreadId, string Status, string Decision, string? Reason, int PolicyVersion, DateTimeOffset At);
    public sealed record AuditRow(DateTimeOffset At, string Category, string Summary);
}
