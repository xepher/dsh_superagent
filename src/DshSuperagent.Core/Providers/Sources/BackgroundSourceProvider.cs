using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Core.Providers.Sources;

/// <summary>Transport workers receive facts through the same intake as callbacks, never grant authority.</summary>
public interface IBackgroundSourceProvider : ISourceProvider
{
    Task RunAsync(Source source, ISourceRuntime runtime, CancellationToken cancellationToken);
}

public sealed record SourceThread(string Key, DateTimeOffset CreatedAt, ChannelAddress Address);

public interface ISourceRuntime
{
    Task ReceiveAsync(InboundEvent inbound, CancellationToken cancellationToken);
    Task<string?> ReadStateAsync(string key, CancellationToken cancellationToken);
    Task WriteStateAsync(string key, string? value, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> ReadStatePrefixAsync(string prefix, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceThread>> GetSubscribedThreadsAsync(CancellationToken cancellationToken);
    Task ObservePullRequestAsync(string threadKey, bool merged, bool closed, string url, CancellationToken cancellationToken);
    Task ReportAsync(string component, string status, string detail, CancellationToken cancellationToken);
}

/// <summary>Only URLs in this Source's configured repository may become followed PR conversations.</summary>
public interface IPullRequestSourceProvider : IBackgroundSourceProvider
{
    ChannelAddress? AddressForPullRequest(Source source, string url);
}

public sealed class ChannelDestinationGoneException(string message) : InvalidOperationException(message);
