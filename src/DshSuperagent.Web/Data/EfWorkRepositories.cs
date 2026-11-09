using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Data;

public sealed class EfTicketRepository(AppDbContext db) : ITicketRepository
{
    public Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Tickets
            .Include(ticket => ticket.Inputs)
            .Include(ticket => ticket.Assignments)
            .Include(ticket => ticket.Reports)
            .FirstOrDefaultAsync(ticket => ticket.Id == id, cancellationToken);

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken) =>
        await db.Tickets.AddAsync(ticket, cancellationToken);

    public async Task<IReadOnlyList<Ticket>> GetTicketsForThreadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var ticketIds = await db.Subscriptions
            .Where(subscription => subscription.ThreadId == threadId && subscription.DroppedAt == null)
            .Select(subscription => subscription.TicketId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return await db.Tickets
            .Where(ticket => ticketIds.Contains(ticket.Id))
            .Include(ticket => ticket.Inputs)
            .Include(ticket => ticket.Assignments)
            .Include(ticket => ticket.Reports)
            .OrderBy(ticket => ticket.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HasAnyTicketForThreadAsync(Guid threadId, CancellationToken cancellationToken) =>
        db.Subscriptions.AnyAsync(
            subscription => subscription.ThreadId == threadId && subscription.DroppedAt == null,
            cancellationToken);

    public Task<bool> HasUnreadInputsAsync(
        Guid ticketId,
        IReadOnlyCollection<Guid> excludedInputIds,
        CancellationToken cancellationToken) =>
        db.TicketInputs.AnyAsync(
            input => input.TicketId == ticketId
                && input.ReadAt == null
                && !excludedInputIds.Contains(input.Id),
            cancellationToken);

    public Task<bool> HasQueuedAssignmentAsync(
        Guid ticketId,
        Guid excludedAssignmentId,
        CancellationToken cancellationToken) =>
        db.Assignments.AnyAsync(
            assignment => assignment.TicketId == ticketId
                && assignment.Id != excludedAssignmentId
                && (assignment.Status == AssignmentStatus.Pending || assignment.Status == AssignmentStatus.Running),
            cancellationToken);

    public async Task<Ticket?> GetLatestForThreadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var ticketId = await db.Subscriptions
            .Where(subscription => subscription.ThreadId == threadId && subscription.DroppedAt == null)
            .OrderByDescending(subscription => subscription.CreatedAt)
            .Select(subscription => subscription.TicketId)
            .FirstOrDefaultAsync(cancellationToken);
        if (ticketId == Guid.Empty)
        {
            return null;
        }

        return await GetAsync(ticketId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetQueuedAssignmentTicketIdsAsync(CancellationToken cancellationToken) =>
        await db.Assignments
            .Where(assignment => assignment.Status == AssignmentStatus.Pending)
            .OrderBy(assignment => assignment.CreatedAt)
            .Select(assignment => assignment.TicketId)
            .Distinct()
            .Take(200)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Ticket>> GetOpenTicketsAsync(CancellationToken cancellationToken) =>
        await db.Tickets
            .Where(ticket => ticket.Status == TicketStatus.Open)
            .Include(ticket => ticket.Inputs)
            .Include(ticket => ticket.Assignments)
            .Include(ticket => ticket.Reports)
            .ToListAsync(cancellationToken);
}

public sealed class EfChannelRepository(AppDbContext db) : IChannelRepository
{
    public Task<Channel?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Channels.FirstOrDefaultAsync(channel => channel.Id == id, cancellationToken);

    public async Task AddAsync(Channel channel, CancellationToken cancellationToken) =>
        await db.Channels.AddAsync(channel, cancellationToken);

    public async Task AddMessageAsync(ChannelMessage message, CancellationToken cancellationToken) =>
        await db.ChannelMessages.AddAsync(message, cancellationToken);

    public Task<ChannelMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken) =>
        db.ChannelMessages.FirstOrDefaultAsync(message => message.Id == id, cancellationToken);

    public Task<bool> HasReplyAsync(Guid channelId, CancellationToken cancellationToken) =>
        db.ChannelMessages.AnyAsync(
            message => message.ChannelId == channelId
                && (message.Status == MessageStatus.Sending
                    || message.Status == MessageStatus.Sent
                    || message.Status == MessageStatus.AwaitingPull),
            cancellationToken);

    public Task<bool> HasQueuedMessageAsync(Guid channelId, CancellationToken cancellationToken) =>
        db.ChannelMessages.AnyAsync(
            message => message.ChannelId == channelId
                && (message.Status == MessageStatus.Pending || message.Status == MessageStatus.Sending),
            cancellationToken);

    public async Task<IReadOnlyList<Channel>> GetThreadChannelsAsync(
        Guid threadId,
        CancellationToken cancellationToken) =>
        await db.Channels
            .Where(channel => channel.ThreadId == threadId)
            .OrderBy(channel => channel.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ChannelMessage>> GetDueMessagesAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await db.ChannelMessages
            .Where(message => message.Status == MessageStatus.Pending && message.NextAttemptAt <= now)
            .OrderBy(message => message.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ChannelMessage>> GetMessagesForTicketAsync(
        Guid ticketId,
        CancellationToken cancellationToken) =>
        await db.ChannelMessages
            .Where(message => message.TicketId == ticketId)
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);
}

public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
