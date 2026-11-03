using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using Microsoft.EntityFrameworkCore;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Web.Data;

public sealed class EfSourceRepository(AppDbContext db) : ISourceRepository
{
    public Task<Source?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Sources.FirstOrDefaultAsync(source => source.Id == id, cancellationToken);

    public Task<Source?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        db.Sources.FirstOrDefaultAsync(source => source.Name == name, cancellationToken);

    public async Task<IReadOnlyList<Source>> ListAsync(CancellationToken cancellationToken) =>
        await db.Sources.OrderBy(source => source.Name).ToListAsync(cancellationToken);

    public async Task AddAsync(Source source, CancellationToken cancellationToken) =>
        await db.Sources.AddAsync(source, cancellationToken);
}

public sealed class EfPolicyRepository(AppDbContext db) : IPolicyRepository
{
    public async Task<Policy> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var policy = await db.Policies
            .Include(item => item.Rules)
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (policy is not null)
        {
            return policy;
        }

        policy = Policy.CreateDefault();
        await db.Policies.AddAsync(policy, cancellationToken);
        return policy;
    }
}

public sealed class EfThreadRepository(AppDbContext db) : IThreadRepository
{
    public Task<Thread?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Threads
            .Include(thread => thread.Subscriptions)
            .FirstOrDefaultAsync(thread => thread.Id == id, cancellationToken);

    public Task<Thread?> FindByExternalKeyAsync(Guid sourceId, string externalKey, CancellationToken cancellationToken) =>
        db.Threads
            .Include(thread => thread.Subscriptions)
            .FirstOrDefaultAsync(
                thread => thread.SourceId == sourceId && thread.ExternalKey == externalKey,
                cancellationToken);

    public async Task AddThreadAsync(Thread thread, CancellationToken cancellationToken) =>
        await db.Threads.AddAsync(thread, cancellationToken);

    public Task<Trigger?> FindTriggerAsync(Guid sourceId, string externalId, CancellationToken cancellationToken) =>
        db.Triggers.FirstOrDefaultAsync(
            trigger => trigger.SourceId == sourceId && trigger.ExternalId == externalId,
            cancellationToken);

    public Task<Trigger?> GetTriggerAsync(Guid id, CancellationToken cancellationToken) =>
        db.Triggers.FirstOrDefaultAsync(trigger => trigger.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Trigger>> GetTriggersAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.ToList();
        return await db.Triggers
            .Where(trigger => list.Contains(trigger.Id))
            .OrderBy(trigger => trigger.ReceivedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Trigger?> GetLatestTriggerForThreadAsync(Guid threadId, CancellationToken cancellationToken) =>
        await db.Triggers
            .Where(trigger => trigger.ThreadId == threadId)
            .OrderByDescending(trigger => trigger.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddTriggerAsync(Trigger trigger, CancellationToken cancellationToken) =>
        await db.Triggers.AddAsync(trigger, cancellationToken);

    public async Task AddDispositionAsync(Disposition disposition, CancellationToken cancellationToken) =>
        await db.Dispositions.AddAsync(disposition, cancellationToken);

    public Task<Disposition?> GetLatestDispositionAsync(Guid triggerId, CancellationToken cancellationToken) =>
        db.Dispositions.AsNoTracking()
            .Where(disposition => disposition.TriggerId == triggerId)
            .OrderByDescending(disposition => disposition.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddCorrelationAsync(Correlation correlation, CancellationToken cancellationToken) =>
        await db.Correlations.AddAsync(correlation, cancellationToken);

    public Task<Eval?> GetEvalAsync(Guid id, CancellationToken cancellationToken) =>
        db.Evals.FirstOrDefaultAsync(eval => eval.Id == id, cancellationToken);

    public async Task AddEvalAsync(Eval eval, CancellationToken cancellationToken) =>
        await db.Evals.AddAsync(eval, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetQueuedEvalIdsAsync(CancellationToken cancellationToken) =>
        await db.Evals
            .Where(eval => eval.Status == Domain.Authorization.EvalStatus.Queued)
            .OrderBy(eval => eval.CreatedAt)
            .Take(200)
            .Select(eval => eval.Id)
            .ToListAsync(cancellationToken);

    public Task<Eval?> GetActiveEvalAsync(Guid threadId, CancellationToken cancellationToken) =>
        db.Evals
            .Where(eval => eval.ThreadId == threadId
                           && (eval.Status == Domain.Authorization.EvalStatus.Queued
                               || eval.Status == Domain.Authorization.EvalStatus.Running))
            .OrderByDescending(eval => eval.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Eval>> GetRunningEvalsAsync(CancellationToken cancellationToken) =>
        await db.Evals
            .Where(eval => eval.Status == Domain.Authorization.EvalStatus.Queued
                           || eval.Status == Domain.Authorization.EvalStatus.Running)
            .ToListAsync(cancellationToken);
}
