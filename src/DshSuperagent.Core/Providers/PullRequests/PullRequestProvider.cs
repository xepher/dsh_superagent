using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;

namespace DshSuperagent.Core.Providers.PullRequests;

public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
}

public sealed record PullRequestFile(string Path, FileChangeKind Kind, string? Content);

public sealed record PullRequestRequest(
    string Owner,
    string Repository,
    string BaseBranch,
    string BranchName,
    string Title,
    string Body,
    string CommitMessage,
    IReadOnlyList<PullRequestFile> Files,
    bool Draft,
    IReadOnlyList<string> Labels);

public sealed record PullRequestResult(
    bool Created,
    string? Url,
    string? Number,
    string? Message,
    string? Patch = null);

public interface IPullRequestProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    Task<PullRequestResult> CreateAsync(
        PullRequestProfile profile,
        PullRequestRequest request,
        CancellationToken cancellationToken);

    Task<ProviderCheckResult> CheckAsync(
        PullRequestProfile profile,
        CancellationToken cancellationToken);
}

public sealed class PullRequestProviderRegistry
{
    private readonly Dictionary<string, IPullRequestProvider> _providers;

    public PullRequestProviderRegistry(IEnumerable<IPullRequestProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IPullRequestProvider> All => _providers.Values;

    public IPullRequestProvider? Find(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;
}
