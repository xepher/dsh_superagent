using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Core.Application;

/// <summary>
/// Stored outgoing work. A message is persisted before sending, retried with backoff, and dropped
/// only when its destination can never receive it. The domain stores the message; the provider
/// owns transport, including whether a write reaches the target or only stages it for a pull.
/// </summary>
public sealed class ChannelDeliveryService(
    IChannelRepository channels,
    ITicketRepository tickets,
    ISourceRepository sources,
    SourceProviderRegistry providers,
    IUnitOfWork unitOfWork)
{
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        var due = await channels.GetDueMessagesAsync(20, DateTimeOffset.UtcNow, cancellationToken);
        var delivered = 0;
        var written = new HashSet<Guid>();
        foreach (var message in due)
        {
            var channel = await channels.GetAsync(message.ChannelId, cancellationToken);
            if (channel is null)
            {
                message.MarkDropped("Channel 已不存在。");
                continue;
            }

            var source = await sources.GetAsync(channel.SourceId, cancellationToken);
            if (source is null)
            {
                message.MarkDropped("Source 已不存在。");
                continue;
            }

            if (!source.Enabled)
            {
                message.Defer(TimeSpan.FromMinutes(1));
                continue;
            }

            var provider = providers.Find(source.ProviderId);
            if (provider is null)
            {
                message.MarkDropped($"Source Provider {source.ProviderId} 未注册。");
                continue;
            }

            if (channel.Purpose == ChannelPurpose.None
                || string.Equals(ChannelAddress.FromJson(channel.AddressJson).Kind, "none", StringComparison.OrdinalIgnoreCase))
            {
                message.MarkDropped("该 Channel 没有可写的对话地址。");
                continue;
            }

            var singleUse = provider.Capabilities.SingleUseReply;
            if (singleUse
                && (written.Contains(channel.Id) || await channels.HasReplyAsync(channel.Id, cancellationToken)))
            {
                await DropAsync(message, "一次性应答地址已被使用：该地址只能承载一条回复，第二条无法送达。", cancellationToken);
                continue;
            }

            message.BeginSending();
            try
            {
                var address = ChannelAddress.FromJson(channel.AddressJson);
                address.Data["deliveryId"] = message.Id.ToString("N");
                var outcome = await provider.SendAsync(
                    source,
                    address,
                    message.Content,
                    cancellationToken);
                if (outcome == DeliveryOutcome.Delivered)
                {
                    message.MarkDelivered();
                    delivered++;
                }
                else
                {
                    message.MarkAwaitingPull();
                }

                if (singleUse)
                {
                    written.Add(channel.Id);
                }
            }
            catch (ChannelDestinationGoneException ex)
            {
                message.MarkDropped(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delaySeconds = Math.Min(300, 5 * (int)Math.Pow(2, Math.Min(message.Attempts, 6)));
                message.MarkFailed(ex.Message, TimeSpan.FromSeconds(delaySeconds));
                if (message.Attempts >= 3)
                {
                    message.MarkDropped($"连续失败 3 次，已停止重试：{ex.Message}");
                }
                else
                {
                    message.RequeueAfterFailure();
                }
            }

            // Cleanup of superseded bubbles: best effort, and never allowed to change the outcome above.
            if (singleUse && message.Status is MessageStatus.Sent or MessageStatus.AwaitingPull)
            {
                try
                {
                    await CloseSupersededAsync(provider, source, channel, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed cleanup must not discard the deliveries already recorded in this batch.
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return delivered;
    }

    private async Task DropAsync(ChannelMessage message, string reason, CancellationToken cancellationToken)
    {
        message.MarkDropped(reason);
        if (message.TicketId is { } ticketId
            && await tickets.GetAsync(ticketId, cancellationToken) is { } ticket)
        {
            ticket.MarkReplyUndeliverable(reason);
        }
    }

    /// <summary>
    /// A single-use bubble older than the one now answered asked a question this ticket will never
    /// answer. End it on whatever it already shows so its client stops waiting; newer bubbles belong
    /// to questions still being answered and must be left alone.
    /// </summary>
    private async Task CloseSupersededAsync(
        ISourceProvider provider,
        Source source,
        Channel answered,
        CancellationToken cancellationToken)
    {
        var siblings = await channels.GetThreadChannelsAsync(answered.ThreadId, cancellationToken);
        foreach (var sibling in siblings)
        {
            if (sibling.Id == answered.Id
                || sibling.CreatedAt >= answered.CreatedAt
                || sibling.Purpose == ChannelPurpose.None)
            {
                continue;
            }

            if (await channels.HasReplyAsync(sibling.Id, cancellationToken)
                || await channels.HasQueuedMessageAsync(sibling.Id, cancellationToken))
            {
                continue;
            }

            try
            {
                await provider.CloseAsync(source, ChannelAddress.FromJson(sibling.AddressJson), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leaving one bubble waiting is preferable to failing the answer that was just written.
            }
        }
    }
}
