using DshSuperagent.Core.Configuration;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Core.Storage;

// Ports are owned by the application layer; adapters (EF, WeCom, GitHub) live in outer layers.

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IDomainEventSink
{
    Task DispatchAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken);
}

public interface IConfigurationStore
{
    Task<RepositoryProfile?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken);
    Task<RepositoryProfile?> GetRepositoryBySlugAsync(string slug, CancellationToken cancellationToken);
    Task<IReadOnlyList<RepositoryProfile>> GetRepositoriesAsync(bool enabledOnly, CancellationToken cancellationToken);
    Task<LlmProfile?> GetDefaultLlmAsync(CancellationToken cancellationToken);
    Task<PullRequestProfile?> GetDefaultPullRequestAsync(CancellationToken cancellationToken);
    Task<PullRequestProfile?> GetPullRequestProfileAsync(Guid id, CancellationToken cancellationToken);
    Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken);
    Task<TeamPrompts> GetTeamPromptsAsync(CancellationToken cancellationToken);
}

public interface ISourceRepository
{
    Task<Source?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Source?> FindByNameAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<Source>> ListAsync(CancellationToken cancellationToken);
    Task AddAsync(Source source, CancellationToken cancellationToken);
}

public interface IPolicyRepository
{
    Task<Policy> GetCurrentAsync(CancellationToken cancellationToken);
}

public interface IThreadRepository
{
    Task<Thread?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Thread?> FindByExternalKeyAsync(Guid sourceId, string externalKey, CancellationToken cancellationToken);
    Task AddThreadAsync(Thread thread, CancellationToken cancellationToken);

    Task<Trigger?> FindTriggerAsync(Guid sourceId, string externalId, CancellationToken cancellationToken);
    Task<Trigger?> GetTriggerAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Trigger>> GetTriggersAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    Task<Trigger?> GetLatestTriggerForThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task AddTriggerAsync(Trigger trigger, CancellationToken cancellationToken);
    Task AddDispositionAsync(Disposition disposition, CancellationToken cancellationToken);
    Task<Disposition?> GetLatestDispositionAsync(Guid triggerId, CancellationToken cancellationToken);
    Task AddCorrelationAsync(Correlation correlation, CancellationToken cancellationToken);

    Task<Eval?> GetEvalAsync(Guid id, CancellationToken cancellationToken);
    Task AddEvalAsync(Eval eval, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetQueuedEvalIdsAsync(CancellationToken cancellationToken);
    Task<Eval?> GetActiveEvalAsync(Guid threadId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Eval>> GetRunningEvalsAsync(CancellationToken cancellationToken);
}

public interface ITicketRepository
{
    Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(Ticket ticket, CancellationToken cancellationToken);
    Task<IReadOnlyList<Ticket>> GetTicketsForThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<bool> HasAnyTicketForThreadAsync(Guid threadId, CancellationToken cancellationToken);

    /// <summary>Read from storage, not an in-memory snapshot: a question may arrive mid-execution.</summary>
    Task<bool> HasUnreadInputsAsync(
        Guid ticketId,
        IReadOnlyCollection<Guid> excludedInputIds,
        CancellationToken cancellationToken);

    /// <summary>Another assignment is already queued or running for this ticket, outside this scope.</summary>
    Task<bool> HasQueuedAssignmentAsync(
        Guid ticketId,
        Guid excludedAssignmentId,
        CancellationToken cancellationToken);
    Task<Ticket?> GetLatestForThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetQueuedAssignmentTicketIdsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Ticket>> GetOpenTicketsAsync(CancellationToken cancellationToken);
}

public interface IChannelRepository
{
    Task<Channel?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(Channel channel, CancellationToken cancellationToken);
    Task AddMessageAsync(ChannelMessage message, CancellationToken cancellationToken);
    Task<ChannelMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the address already carries a reply. An in-flight send counts: a second write to the
    /// same single-use address would race with it.
    /// </summary>
    Task<bool> HasReplyAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>Whether a queued message still needs this address.</summary>
    Task<bool> HasQueuedMessageAsync(Guid channelId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Channel>> GetThreadChannelsAsync(Guid threadId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChannelMessage>> GetDueMessagesAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChannelMessage>> GetMessagesForTicketAsync(Guid ticketId, CancellationToken cancellationToken);
}

public enum WorkItemKind
{
    Eval,
    Assignment,
    Scan,
}

public sealed record WorkItem(WorkItemKind Kind, Guid Id);

public interface IWorkQueue
{
    ValueTask EnqueueAsync(WorkItem item, CancellationToken cancellationToken);
    ValueTask<WorkItem> DequeueAsync(CancellationToken cancellationToken);
}
