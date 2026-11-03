using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;

namespace DshSuperagent.Core.Application;

/// <summary>
/// Stored outgoing work. A message is persisted before sending, retried with backoff, and dropped
/// only when its destination can never receive it. The domain stores the message; the provider
/// owns transport.
/// </summary>
public sealed class ChannelDeliveryService(
    IChannelRepository channels,
    ISourceRepository sources,
    SourceProviderRegistry providers,
    IUnitOfWork unitOfWork)
{
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        var due = await channels.GetDueMessagesAsync(20, DateTimeOffset.UtcNow, cancellationToken);
        var delivered = 0;
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

            message.BeginSending();
            try
            {
                await provider.SendAsync(
                    source,
                    ChannelAddress.FromJson(channel.AddressJson),
                    message.Content,
                    cancellationToken);
                message.MarkSent();
                delivered++;
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
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return delivered;
    }
}
