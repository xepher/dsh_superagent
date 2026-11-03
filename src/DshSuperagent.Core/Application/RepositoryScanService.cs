using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Review;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Core.Application;

/// <summary>
/// A Source implementation, not a hidden second workflow: each scan finding becomes an immutable
/// Trigger. Gate + Eval decide whether it becomes a fix Ticket, exactly like a human request.
/// </summary>
public sealed class RepositoryScanService(
    ISourceRepository sources,
    IPolicyRepository policies,
    IThreadRepository threads,
    IChannelRepository channels,
    IConfigurationStore configuration,
    IWorkQueue queue,
    IDomainEventSink domainEvents,
    IUnitOfWork unitOfWork)
{
    public const string ScanThreadPrefix = "scan:";

    public async Task<int> ScanAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await configuration.GetRepositoryAsync(repositoryId, cancellationToken);
        if (repository is null || !repository.Enabled)
        {
            return 0;
        }

        var source = await sources.FindByNameAsync($"scan:{repository.Slug}", cancellationToken);
        if (source is null)
        {
            source = Source.Create(
                RepositoryScanSourceProvider.ProviderId,
                $"scan:{repository.Slug}",
                new Dictionary<string, string>(),
                repository.Id);
            await sources.AddAsync(source, cancellationToken);
        }
        else if (!source.Enabled)
        {
            return 0;
        }

        var thread = await threads.FindByExternalKeyAsync(source.Id, $"{ScanThreadPrefix}{repository.Id}", cancellationToken);
        if (thread is null)
        {
            thread = Thread.Open(source.Id, $"{ScanThreadPrefix}{repository.Id}", $"代码扫描 · {repository.DisplayName}");
            await threads.AddThreadAsync(thread, cancellationToken);
        }

        var settings = await configuration.GetAgentSettingsAsync(cancellationToken);
        var reviewer = new StaticCodeReviewer();
        var scan = await reviewer.ScanAsync(repository, settings.MaxReviewFiles, cancellationToken);
        var commit = await ReadCommitAsync(repository, cancellationToken);
        var policy = await policies.GetCurrentAsync(cancellationToken);
        var sourcePolicy = policy.GetSourcePolicy(source.Id);
        var touched = new List<AggregateRoot> { thread };
        var created = 0;
        var evalIds = new List<Guid>();
        var autoFixBudget = policy.AcceptMode == AcceptMode.Auto && repository.AllowAutonomousFix
            ? Math.Clamp(settings.MaxAutoFixesPerRun, 1, 5)
            : int.MaxValue;

        foreach (var finding in scan.Findings
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.FilePath)
                     .ThenBy(item => item.Line)
                     .Take(30))
        {
            var actor = new TriggerActor("repository-scan", "定时代码审查", ActorKind.System);
            var externalId = $"finding:{commit}:{finding.Signature()}";
            var existing = await threads.FindTriggerAsync(source.Id, externalId, cancellationToken);
            if (existing is not null)
            {
                if (autoFixBudget > 0)
                {
                    var last = await threads.GetLatestDispositionAsync(existing.Id, cancellationToken);
                    if (last is { Kind: DispositionKind.Filtered }
                        && last.Reason.Contains("自动修复上限", StringComparison.Ordinal))
                    {
                        autoFixBudget--;
                        await ScheduleExistingAsync(thread, existing, policy, evalIds, touched, cancellationToken);
                    }
                }

                continue;
            }

            var channel = new Channel(
                thread.Id,
                source.Id,
                source.ProviderId,
                ChannelAddress.Create("none").ToJson(),
                ChannelPurpose.None);
            await channels.AddAsync(channel, cancellationToken);

            var gate = policy.Gate(sourcePolicy, actor, addressed: false, threadHasTicket: false);
            var payload = JsonSerializer.Serialize(new
            {
                scan = new { commit, repository = repository.Slug, files = scan.FilesScanned },
                finding = new
                {
                    rule_id = finding.RuleId,
                    severity = finding.Severity.ToString(),
                    file = finding.FilePath,
                    line = finding.Line,
                    title = finding.Title,
                    evidence = finding.Evidence,
                    explanation = finding.Explanation,
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var trigger = Trigger.Record(
                source.Id,
                thread.Id,
                channel.Id,
                externalId,
                finding.Title,
                actor,
                $"静态审查发现：{finding.Title}\n{finding.Evidence}",
                addressed: false,
                gate.Strength,
                "scan",
                payload,
                null,
                null,
                DateTimeOffset.UtcNow);
            await threads.AddTriggerAsync(trigger, cancellationToken);
            thread.Touch(trigger.ReceivedAt);
            created++;

            if (!gate.IsAdmitted)
            {
                await threads.AddDispositionAsync(
                    new Disposition(trigger.Id, thread.Id, DispositionKind.Filtered, gate.Reason),
                    cancellationToken);
                continue;
            }

            if (finding.Severity == FindingSeverity.Info)
            {
                await threads.AddDispositionAsync(
                    new Disposition(trigger.Id, thread.Id, DispositionKind.Filtered, "Info 级发现只记录，不进入 Eval。"),
                    cancellationToken);
                continue;
            }

            if (autoFixBudget <= 0)
            {
                await threads.AddDispositionAsync(
                    new Disposition(trigger.Id, thread.Id, DispositionKind.Filtered, "达到本批自动修复上限，本次只记录发现。"),
                    cancellationToken);
                continue;
            }

            autoFixBudget--;

            if (thread.ActiveEvalId is not null)
            {
                thread.EnqueuePendingTrigger(trigger.Id);
                await threads.AddDispositionAsync(
                    new Disposition(trigger.Id, thread.Id, DispositionKind.Admitted, "已有 Eval 在运行，问题进入下一批。", thread.ActiveEvalId),
                    cancellationToken);
                continue;
            }

            var eval = Eval.Start(thread.Id, source.Id, policy.Version, [trigger.Id]);
            thread.SetActiveEval(eval.Id);
            await threads.AddEvalAsync(eval, cancellationToken);
            evalIds.Add(eval.Id);
            await threads.AddDispositionAsync(
                new Disposition(trigger.Id, thread.Id, DispositionKind.Evaluated, "静态审查发现进入 Eval。", eval.Id),
                cancellationToken);
            touched.Add(eval);
        }

        source.RecordReadPosition($"commit={commit}; findings={scan.Findings.Count}; at={DateTimeOffset.UtcNow:O}");
        await domainEvents.DispatchAsync(touched, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var evalId in evalIds)
        {
            await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, evalId), cancellationToken);
        }

        return created;
    }

    private async Task ScheduleExistingAsync(
        Thread thread,
        Trigger trigger,
        Domain.Sources.Policy policy,
        List<Guid> evalIds,
        List<AggregateRoot> touched,
        CancellationToken cancellationToken)
    {
        if (thread.ActiveEvalId is not null)
        {
            thread.EnqueuePendingTrigger(trigger.Id);
            await threads.AddDispositionAsync(
                new Disposition(trigger.Id, thread.Id, DispositionKind.Admitted, "已有 Eval 在运行，问题进入下一批。", thread.ActiveEvalId),
                cancellationToken);
            return;
        }

        var eval = Eval.Start(thread.Id, trigger.SourceId, policy.Version, [trigger.Id]);
        thread.SetActiveEval(eval.Id);
        await threads.AddEvalAsync(eval, cancellationToken);
        evalIds.Add(eval.Id);
        await threads.AddDispositionAsync(
            new Disposition(trigger.Id, thread.Id, DispositionKind.Evaluated, "预算提高后重新进入 Eval。", eval.Id),
            cancellationToken);
        touched.Add(eval);
    }

    private static async Task<string> ReadCommitAsync(RepositoryProfile repository, CancellationToken cancellationToken)
    {
        var result = await Repositories.ProcessRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", "HEAD"],
            repository.LocalPath,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        return result.ExitCode == 0 ? result.Output.Trim() : "no-commit";
    }
}
