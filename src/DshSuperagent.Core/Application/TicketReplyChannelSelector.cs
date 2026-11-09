using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Core.Application;

/// <summary>
/// Picks the address an answer must be written to. A ticket aggregates several questions and every
/// question carried its own reply address, so the answer belongs on the newest address that can
/// still carry it — a transport may hand out single-use addresses (for example a WeCom stream id)
/// that stop accepting writes once used.
/// </summary>
public sealed class TicketReplyChannelSelector(
    IChannelRepository channels,
    IThreadRepository threads,
    ISourceRepository sources,
    SourceProviderRegistry providers)
{
    /// <summary>
    /// <paramref name="answered"/> limits the choice to the inputs this execution actually answered:
    /// a question that arrived mid-execution gets its own assignment and its own answer later.
    /// </summary>
    public async Task<Channel?> ResolveAsync(
        Ticket ticket,
        IEnumerable<Input>? answered,
        CancellationToken cancellationToken)
    {
        var source = await sources.GetAsync(ticket.SourceId, cancellationToken);
        var singleUse = source is not null
            && providers.Find(source.ProviderId)?.Capabilities.SingleUseReply == true;

        var candidates = answered ?? [];
        foreach (var input in candidates
                     .Where(input => input.TriggerId is not null)
                     .OrderByDescending(input => input.CreatedAt))
        {
            var trigger = await threads.GetTriggerAsync(input.TriggerId!.Value, cancellationToken);
            if (trigger?.ChannelId is not { } channelId)
            {
                continue;
            }

            var candidate = await AvailableAsync(channelId, singleUse, cancellationToken);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return ticket.OriginChannelId is { } originId
            ? await AvailableAsync(originId, singleUse, cancellationToken)
            : null;
    }

    private async Task<Channel?> AvailableAsync(Guid channelId, bool singleUse, CancellationToken cancellationToken)
    {
        var channel = await channels.GetAsync(channelId, cancellationToken);
        if (channel is null || channel.Purpose == ChannelPurpose.None)
        {
            return null;
        }

        if (!singleUse)
        {
            return channel;
        }

        return await channels.HasReplyAsync(channel.Id, cancellationToken)
               || await channels.HasQueuedMessageAsync(channel.Id, cancellationToken)
            ? null
            : channel;
    }
}
