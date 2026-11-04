using System.Collections.Concurrent;
using DshSuperagent.Core.Application;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;

namespace DshSuperagent.Web.Services;

public sealed class WorkQueueProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkQueue queue,
    ILogger<WorkQueueProcessor> logger) : BackgroundService
{
    private SemaphoreSlim _concurrency = new(2, 2);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var settings = await scope.ServiceProvider
                .GetRequiredService<DshSuperagent.Core.Storage.IConfigurationStore>()
                .GetAgentSettingsAsync(stoppingToken);
            _concurrency = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrentAssignments, 1, 8));
        }

        await RestoreAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            WorkItem item;
            try
            {
                item = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var key = $"{item.Kind}:{item.Id}";
            if (!_inFlight.TryAdd(key, 0))
            {
                RequeueLater(item);
                continue;
            }

            await _concurrency.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchAsync(item, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "处理 {Kind} {Id} 失败", item.Kind, item.Id);
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                    _concurrency.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await WorkRecovery.RecoverAsync(scope.ServiceProvider, recoverAllRunning: true, cancellationToken);
        var threads = scope.ServiceProvider.GetRequiredService<IThreadRepository>();
        var tickets = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        foreach (var evalId in await threads.GetQueuedEvalIdsAsync(cancellationToken))
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, evalId), cancellationToken);
        }

        foreach (var ticketId in await tickets.GetQueuedAssignmentTicketIdsAsync(cancellationToken))
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, ticketId), cancellationToken);
        }
    }

    private void RequeueLater(WorkItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                await queue.EnqueueAsync(item, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "延迟重排 {Kind} {Id} 失败", item.Kind, item.Id);
            }
        }, CancellationToken.None);
    }

    private async Task DispatchAsync(WorkItem item, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        switch (item.Kind)
        {
            case WorkItemKind.Eval:
                await scope.ServiceProvider.GetRequiredService<EvalProcessor>()
                    .ProcessAsync(item.Id, cancellationToken);
                break;
            case WorkItemKind.Assignment:
                await scope.ServiceProvider.GetRequiredService<TicketExecutionService>()
                    .ProcessAsync(item.Id, cancellationToken);
                break;
            case WorkItemKind.Scan:
                await scope.ServiceProvider.GetRequiredService<RepositoryScanService>()
                    .ScanAsync(item.Id, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"未知 WorkItem：{item.Kind}");
        }
    }
}

public sealed class ChannelDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ChannelDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var delivered = await scope.ServiceProvider.GetRequiredService<ChannelDeliveryService>()
                    .DeliverDueAsync(stoppingToken);
                if (delivered > 0)
                {
                    logger.LogInformation("已发送 {Count} 条 Channel 消息", delivered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Channel 消息投递失败");
            }
        }
    }
}

public sealed class RepositoryReviewScheduler(
    IServiceScopeFactory scopeFactory,
    IWorkQueue queue,
    ILogger<RepositoryReviewScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            AgentSettings settings;
            IReadOnlyList<RepositoryProfile> repositories;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var configuration = scope.ServiceProvider.GetRequiredService<IConfigurationStore>();
                settings = await configuration.GetAgentSettingsAsync(stoppingToken);
                repositories = await configuration.GetRepositoriesAsync(enabledOnly: true, stoppingToken);
            }

            if (settings.AutoReviewEnabled)
            {
                foreach (var repository in repositories.Where(repository => repository.AutoReviewEnabled))
                {
                    await queue.EnqueueAsync(new WorkItem(WorkItemKind.Scan, repository.Id), stoppingToken);
                    logger.LogInformation("已排入仓库扫描：{Slug}", repository.Slug);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Clamp(settings.ReviewIntervalHours, 1, 168)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}


/// <summary>Periodic safety net for interrupted work; it never touches a live in-flight item.</summary>
public sealed class StaleWorkRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<StaleWorkRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await WorkRecovery.RecoverAsync(scope.ServiceProvider, recoverAllRunning: false, stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<DshSuperagent.Web.Data.AppDbContext>();
                var recovered = await WorkRecovery.RecoverStuckMessagesAsync(db, stoppingToken);
                if (recovered > 0)
                {
                    logger.LogWarning("恢复了 {Count} 条卡在 Sending 的 Channel 消息", recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "恢复排队/投递工作时失败");
            }
        }
    }
}
