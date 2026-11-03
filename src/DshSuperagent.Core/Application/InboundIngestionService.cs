using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Commands;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Core.Application;

public sealed record IngestResult(int StatusCode, string ContentType, string Body);

// Intake is deliberately transport-agnostic: providers parse; this use case records and routes.
// A person's follow-up on an existing Ticket Thread bypasses Eval; everything else goes through the gate.
// The domain decides ownership and admission; this service only coordinates repositories.

/// <summary>
/// The intake use case: a provider turns transport data into an InboundEvent, then the Source
/// records an immutable Trigger, fans it out to subscriptions, or starts an Eval. The provider
/// never decides authorization or work.
/// </summary>
public sealed class InboundIngestionService(
    SourceProviderRegistry providers,
    ISourceRepository sources,
    IPolicyRepository policies,
    IThreadRepository threads,
    ITicketRepository tickets,
    IChannelRepository channels,
    IConfigurationStore configuration,
    IStreamReplyStore streams,
    IWorkQueue queue,
    IDomainEventSink domainEvents,
    IUnitOfWork unitOfWork)
{
    public async Task<IngestResult> HandleCallbackAsync(
        Guid sourceId,
        ProviderCallbackRequest request,
        CancellationToken cancellationToken)
    {
        var source = await sources.GetAsync(sourceId, cancellationToken);
        if (source is null || !source.Enabled)
        {
            return new IngestResult(404, "text/plain; charset=utf-8", "Source not found or disabled.");
        }

        var provider = providers.Find(source.ProviderId);
        if (provider is null)
        {
            return new IngestResult(500, "text/plain; charset=utf-8", "Source provider is not registered.");
        }

        SourceCallbackResult callback;
        try
        {
            callback = await provider.HandleCallbackAsync(source, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IngestResult(500, "text/plain; charset=utf-8", $"Provider error: {ex.Message}");
        }

        if (callback.StatusCode != 200 || callback.Event is null)
        {
            return new IngestResult(callback.StatusCode, callback.ContentType, callback.Body);
        }

        return await IngestAsync(source, callback, cancellationToken);
    }

    private async Task<IngestResult> IngestAsync(
        Source source,
        SourceCallbackResult callback,
        CancellationToken cancellationToken)
    {
        var inbound = callback.Event!;
        var ackBody = callback.AckBody ?? callback.Body;
        var ackContentType = callback.AckContentType ?? callback.ContentType;

        var policy = await policies.GetCurrentAsync(cancellationToken);
        var thread = await threads.FindByExternalKeyAsync(source.Id, inbound.ThreadKey, cancellationToken);
        if (thread is null)
        {
            thread = Thread.Open(source.Id, inbound.ThreadKey, inbound.ThreadTitle);
            await threads.AddThreadAsync(thread, cancellationToken);
        }

        var existing = await threads.FindTriggerAsync(source.Id, inbound.ExternalId, cancellationToken);
        if (existing is not null)
        {
            return new IngestResult(
                200,
                existing.CallbackAckContentType ?? ackContentType,
                existing.CallbackAckBody ?? ackBody);
        }

        var channelPurpose = string.Equals(inbound.Channel.Kind, "none", StringComparison.OrdinalIgnoreCase)
            ? ChannelPurpose.None
            : ChannelPurpose.ChatReply;
        var channel = new Channel(thread.Id, source.Id, source.ProviderId, inbound.Channel.ToJson(), channelPurpose);
        await channels.AddAsync(channel, cancellationToken);

        if (inbound.Channel.Get("stream_id") is { Length: > 0 } streamId)
        {
            await streams.SetRunningAsync(streamId, "已收到，正在分析代码仓库…", cancellationToken);
        }

        var command = BotCommandParser.Parse(inbound.Text);
        var payload = MergePayload(inbound.PayloadJson, command);
        var sourcePolicy = policy.GetSourcePolicy(source.Id);
        var hasTicket = await tickets.HasAnyTicketForThreadAsync(thread.Id, cancellationToken);
        var gate = policy.Gate(sourcePolicy, inbound.Actor, inbound.Addressed, hasTicket);

        var trigger = Trigger.Record(
            source.Id,
            thread.Id,
            channel.Id,
            inbound.ExternalId,
            string.IsNullOrWhiteSpace(inbound.Title) ? TitleFrom(inbound.Text) : inbound.Title,
            inbound.Actor,
            inbound.Text,
            inbound.Addressed,
            gate.Strength,
            inbound.ReceiptScope,
            payload,
            ackBody,
            ackContentType,
            inbound.ReceivedAt ?? DateTimeOffset.UtcNow);
        await threads.AddTriggerAsync(trigger, cancellationToken);
        thread.Touch(trigger.ReceivedAt);
        var touched = new List<AggregateRoot> { thread };

        if (!gate.IsAdmitted)
        {
            await RecordDispositionAsync(thread, trigger, DispositionKind.Filtered, gate.Reason, null, null, cancellationToken);
            if (inbound.Actor.IsHuman && inbound.Addressed && !sourcePolicy.IsListed(inbound.Actor.Id))
            {
                await QueueMessageAsync(
                    channel,
                    null,
                    MessagePurpose.Notice,
                    $"当前用户 `{inbound.Actor.Id}` 未在 Source 的允许列表中，无法使用代码仓库助手。请联系管理员添加。",
                    cancellationToken);
            }

            await SaveAsync(touched, cancellationToken);
            return new IngestResult(200, ackContentType, ackBody);
        }

        if (command.Kind is BotCommandKind.Help or BotCommandKind.ListRepositories or BotCommandKind.Status or BotCommandKind.Unknown)
        {
            var text = command.Kind switch
            {
                BotCommandKind.ListRepositories => await FormatRepositoriesAsync(source, cancellationToken),
                BotCommandKind.Status => await FormatStatusAsync(source, policy, cancellationToken),
                _ => BotCommandParser.HelpText(source.Name),
            };
            await QueueMessageAsync(channel, null, MessagePurpose.Notice, text, cancellationToken);
            await RecordDispositionAsync(thread, trigger, DispositionKind.Filtered, "命令由 Source 直接回复，不创建工单。", null, null, cancellationToken);
            await SaveAsync(touched, cancellationToken);
            return new IngestResult(200, ackContentType, ackBody);
        }

        if (command.Kind == BotCommandKind.Review)
        {
            var repository = await ResolveRepositoryAsync(source, command.RepositorySlug, cancellationToken);
            if (repository is null)
            {
                await QueueMessageAsync(channel, null, MessagePurpose.Notice, "没有可扫描的代码仓库，请管理员先配置。", cancellationToken);
                await RecordDispositionAsync(thread, trigger, DispositionKind.Filtered, "手动审查缺少仓库。", null, null, cancellationToken);
            }
            else
            {
                await QueueMessageAsync(channel, null, MessagePurpose.Notice, $"开始扫描 `{repository.Slug}`，发现的问题会进入同一个 Eval/工单链路。", cancellationToken);
                await RecordDispositionAsync(thread, trigger, DispositionKind.Admitted, "手动触发仓库扫描。", null, null, cancellationToken);
            }

            await SaveAsync(touched, cancellationToken);
            if (repository is not null)
            {
                await queue.EnqueueAsync(new WorkItem(WorkItemKind.Scan, repository.Id), cancellationToken);
            }

            return new IngestResult(200, ackContentType, ackBody);
        }

        if (command.Kind == BotCommandKind.Stop)
        {
            return await StartEvalAsync(thread, trigger, policy.Version, touched, ackBody, ackContentType, cancellationToken);
        }

        if (inbound.Actor.IsHuman && gate.Strength == TriggerStrength.Explicit && sourcePolicy.IsListed(inbound.Actor.Id))
        {
            var threadTickets = await tickets.GetTicketsForThreadAsync(thread.Id, cancellationToken);
            var owner = SelectOwner(thread, threadTickets);
            foreach (var ticket in threadTickets.Where(ticket => ticket.Status == TicketStatus.Open && ticket.Id != owner?.Id))
            {
                ticket.AddContextInput(InputKind.Trigger, inbound.Text, trigger.Id);
                touched.Add(ticket);
            }

            if (owner is not null)
            {
                var canResume = true;
                if (owner.Status == TicketStatus.Ended)
                {
                    var revalidation = MandateRevalidator.Revalidate(
                        owner.Mandate,
                        source,
                        policy,
                        sourcePolicy,
                        sourceStillListensToThread: true);
                    canResume = revalidation.Ok;
                }

                if (canResume)
                {
                    owner.AddInput(
                        InputKind.Trigger,
                        inbound.Text,
                        trigger.Id,
                        resumeEnded: owner.Status == TicketStatus.Ended);
                    var startAssignment = owner.ActiveAssignment is null;
                    if (startAssignment)
                    {
                        owner.StartAssignment(ExecutorName(owner.WorkKind));
                    }

                    touched.Add(owner);
                    await RecordDispositionAsync(
                        thread,
                        trigger,
                        DispositionKind.OwnedByTicket,
                        "Thread 上已有工单，消息交给 Executor；本次不经过 Eval。",
                        null,
                        owner.Id,
                        cancellationToken);
                    await SaveAsync(touched, cancellationToken);
                    if (startAssignment)
                    {
                        await queue.EnqueueAsync(new WorkItem(WorkItemKind.Assignment, owner.Id), cancellationToken);
                    }

                    return new IngestResult(200, ackContentType, ackBody);
                }
            }
        }

        return await StartEvalAsync(thread, trigger, policy.Version, touched, ackBody, ackContentType, cancellationToken);
    }

    private async Task<IngestResult> StartEvalAsync(
        Thread thread,
        Trigger trigger,
        int policyVersion,
        List<AggregateRoot> touched,
        string ackBody,
        string ackContentType,
        CancellationToken cancellationToken)
    {
        if (thread.ActiveEvalId is not null)
        {
            thread.EnqueuePendingTrigger(trigger.Id);
            await RecordDispositionAsync(thread, trigger, DispositionKind.Admitted, "已有 Eval 在运行，Trigger 进入下一批。", thread.ActiveEvalId, null, cancellationToken);
            await SaveAsync(touched, cancellationToken);
            return new IngestResult(200, ackContentType, ackBody);
        }

        var eval = Eval.Start(thread.Id, trigger.SourceId, policyVersion, [trigger.Id]);
        thread.SetActiveEval(eval.Id);
        await threads.AddEvalAsync(eval, cancellationToken);
        touched.Add(eval);
        await RecordDispositionAsync(thread, trigger, DispositionKind.Evaluated, "进入 Eval。", eval.Id, null, cancellationToken);
        await SaveAsync(touched, cancellationToken);
        await queue.EnqueueAsync(new WorkItem(WorkItemKind.Eval, eval.Id), cancellationToken);
        return new IngestResult(200, ackContentType, ackBody);
    }

    private static Ticket? SelectOwner(Thread thread, IReadOnlyList<Ticket> tickets)
    {
        var candidates = tickets
            .Where(ticket => ticket.OriginThreadId == thread.Id)
            .Where(ticket => thread.Subscriptions.Any(subscription =>
                subscription.IsLive && subscription.TicketId == ticket.Id && !subscription.ContextOnly))
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ToList();
        return candidates.FirstOrDefault(ticket => ticket.Status == TicketStatus.Open)
               ?? candidates.FirstOrDefault(ticket => ticket.Status == TicketStatus.Ended && !ticket.StoppedByPerson);
    }

    private async Task<RepositoryProfile?> ResolveRepositoryAsync(
        Source source,
        string? slug,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return await configuration.GetRepositoryBySlugAsync(slug, cancellationToken);
        }

        if (source.DefaultRepositoryId is { } id)
        {
            var configured = await configuration.GetRepositoryAsync(id, cancellationToken);
            if (configured is { Enabled: true })
            {
                return configured;
            }
        }

        var repositories = await configuration.GetRepositoriesAsync(enabledOnly: true, cancellationToken);
        return repositories.Count == 1 ? repositories[0] : null;
    }

    private async Task<string> FormatRepositoriesAsync(Source source, CancellationToken cancellationToken)
    {
        var repositories = await configuration.GetRepositoriesAsync(enabledOnly: true, cancellationToken);
        if (repositories.Count == 0)
        {
            return "还没有启用任何代码仓库。请管理员在「代码仓库」中添加。";
        }

        var lines = repositories.Select(repository =>
            $"- `{repository.Slug}` — {repository.DisplayName}{(repository.Id == source.DefaultRepositoryId ? "（默认）" : string.Empty)}");
        return "**可用代码仓库**\n\n" + string.Join('\n', lines) + "\n\n指定仓库提问：`/repo <slug> <问题>`";
    }

    private async Task<string> FormatStatusAsync(Source source, Domain.Sources.Policy policy, CancellationToken cancellationToken)
    {
        var llm = await configuration.GetDefaultLlmAsync(cancellationToken);
        var pr = await configuration.GetDefaultPullRequestAsync(cancellationToken);
        var repositories = await configuration.GetRepositoriesAsync(enabledOnly: true, cancellationToken);
        return $"""
            **{source.Name} 状态**

            - Provider：`{source.ProviderId}`
            - Policy：v{policy.Version}，accept mode `{policy.AcceptMode}`
            - 已启用仓库：{repositories.Count} 个
            - 默认模型：{(llm is null ? "未配置（问答降级为本地检索）" : $"`{llm.Name}`")}
            - PR Provider：{(pr is null ? "未配置（补丁只保存）" : $"`{pr.Name}`")}
            """;
    }

    private async Task RecordDispositionAsync(
        Thread thread,
        Trigger trigger,
        DispositionKind kind,
        string reason,
        Guid? evalId,
        Guid? ticketId,
        CancellationToken cancellationToken)
    {
        await threads.AddDispositionAsync(
            new Disposition(trigger.Id, thread.Id, kind, reason, evalId, ticketId),
            cancellationToken);
    }

    private async Task QueueMessageAsync(
        Channel channel,
        Guid? ticketId,
        MessagePurpose purpose,
        string content,
        CancellationToken cancellationToken)
    {
        if (channel.Purpose == ChannelPurpose.None)
        {
            return;
        }

        await channels.AddMessageAsync(new ChannelMessage(channel.Id, ticketId, purpose, content), cancellationToken);
    }

    private async Task SaveAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken)
    {
        var list = aggregates.Distinct().ToList();
        await domainEvents.DispatchAsync(list, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static string MergePayload(string? payloadJson, BotCommand command)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            payload = new JsonObject();
        }

        payload["command"] = command.Kind.ToString();
        if (!string.IsNullOrWhiteSpace(command.RepositorySlug))
        {
            payload["repository_slug"] = command.RepositorySlug;
        }

        if (command.FindingId is { } findingId)
        {
            payload["finding_id"] = findingId.ToString();
        }

        return payload.ToJsonString();
    }

    private static string TitleFrom(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static string ExecutorName(WorkKind kind) =>
        kind == WorkKind.Fix ? "repository-fix" : "repository-question";
}
