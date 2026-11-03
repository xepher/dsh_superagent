using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Core.Application;

/// <summary>
/// A Source provider with no transport. The scheduled scan service uses the repository adapter to
/// produce Triggers; this provider only makes that Source visible and configurable in the panel.
/// </summary>
public sealed class RepositoryScanSourceProvider : ISourceProvider
{
    public const string ProviderId = "repository-scan";

    public string Id => ProviderId;
    public string DisplayName => "代码仓库扫描";
    public string Description => "定时或手动执行静态审查，把发现的问题作为推断候选 Trigger 送入同一条 Eval/工单链路。";
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("note", "说明", ProviderFieldType.Text, Help: "扫描仓库由「代码仓库」中的配置决定；这里只用于展示。"),
    ];

    public SourceCapabilities Capabilities => new(Scheduled: true);

    public Task<SourceCallbackResult> HandleCallbackAsync(
        Source source,
        ProviderCallbackRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(SourceCallbackResult.BadRequest("代码仓库扫描 Source 不接收 HTTP 回调。"));

    public Task SendAsync(
        Source source,
        ChannelAddress address,
        string markdown,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<ProviderCheckResult> CheckAsync(Source source, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCheckResult(true, "扫描 Source 由定时任务和「立即扫描」触发。"));
}
