using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.Common;

namespace DshSuperagent.Providers.Gitea;

public sealed class GiteaSourceProvider(IHttpClientFactory clients) : CodeHostSourceProvider
{
    public override string Id => "gitea";
    public override string DisplayName => "Gitea（GitHub 样式来源）";
    public override string Description => "参照原版 GitHub 轮询仓库通知、Issue/PR、审查和提交状态；使用 Gitea REST API，不冒充 gh 兼容。";
    protected override bool Github => false;
    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("endpoint", "Gitea API 地址", ProviderFieldType.Text, Required: true, Placeholder: "https://git.example.com/api/v1",
            Help: "填写完整 /api/v1 地址；非回环实例必须使用 HTTPS，不自动跟随重定向。"),
        new("token", "智能体账号 Token", ProviderFieldType.Secret, Required: true),
        .. PollFields,
    ];

    private static Uri Endpoint(Source source)
    {
        if (!Uri.TryCreate(source.Required("endpoint"), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || (uri.Scheme == "http" && !uri.IsLoopback) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.Ordinal))
            throw new InvalidOperationException("Gitea API 地址必须是无凭据、查询和片段的 HTTP(S) /api/v1 地址。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    protected override string WebBase(Source source) => Endpoint(source).AbsoluteUri[..^"api/v1/".Length].TrimEnd('/');

    protected override async Task<CodeHostResponse> ApiAsync(Source source, string method, string path, object? body, CancellationToken token)
    {
        var endpoint = Endpoint(source);
        if (path.StartsWith('/') || Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new InvalidOperationException("Gitea API 路径必须限制在配置端点内。");
        var uri = new Uri(endpoint, path);
        if (!uri.AbsoluteUri.StartsWith(endpoint.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Gitea API 路径越过配置端点。");
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", source.Required("token"));
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body);
        var client = clients.CreateClient("gitea");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, budget.Token);
            var text = await response.Content.ReadAsStringAsync(budget.Token);
            if (!response.IsSuccessStatusCode)
                throw new CodeHostException((int)response.StatusCode, text.Contains("locked", StringComparison.OrdinalIgnoreCase));
            var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key,
                pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            return new CodeHostResponse(SourceJson.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text), headers);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gitea API 响应头和正文读取合计超过 60 秒，将退避重试。");
        }
    }
}
