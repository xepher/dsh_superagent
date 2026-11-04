using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Providers.Sources;

namespace DshSuperagent.Providers.Gitea;

public sealed class GiteaPullRequestProvider(IHttpClientFactory httpClientFactory) : IPullRequestProvider
{
    public const string ProviderId = "gitea";

    public string Id => ProviderId;
    public string DisplayName => "Gitea Pull Request";
    public string Description => "通过 Gitea Contents API 批量提交文件并创建 PR，无需本地推送凭据。仓库需显式配置 PR Owner/Repository。";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("token", "Gitea Token", ProviderFieldType.Secret,
            Help: "需要目标仓库的代码和 PR 读写权限；连接检查还需读取用户信息。dry-run 时可不填。"),
        new("apiBaseUrl", "API 地址", ProviderFieldType.Text, Required: true,
            Help: "完整 API 根地址，例如 https://git.example.com/api/v1（包含部署子路径）。HTTPS；HTTP 仅允许本机回环地址。"),
        new("dryRun", "Dry-run（不创建分支/PR）", ProviderFieldType.Bool, DefaultValue: "true"),
        new("draft", "创建 WIP PR", ProviderFieldType.Bool, DefaultValue: "false",
            Help: "使用 WIP: 标题前缀；Gitea 服务器需保留默认 WIP 前缀配置。"),
        new("labels", "PR 标签", ProviderFieldType.Text,
            Help: "可选，多个已有仓库标签名用逗号分隔；找不到标签时停止创建。"),
        new("authorName", "提交作者名称", ProviderFieldType.Text, DefaultValue: "dsh-superagent"),
        new("authorEmail", "提交作者邮箱", ProviderFieldType.Text, DefaultValue: "dsh-superagent@localhost"),
    ];

    public async Task<PullRequestResult> CreateAsync(
        PullRequestProfile profile,
        PullRequestRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        if (IsDryRun(profile))
        {
            return new PullRequestResult(false, null, null, "dry-run 已开启，未创建分支或 PR。");
        }

        var settings = ReadSettings(profile);
        using var client = httpClientFactory.CreateClient(ProviderId);
        var repo = $"repos/{Uri.EscapeDataString(request.Owner)}/{Uri.EscapeDataString(request.Repository)}";

        // List all states so retries can reuse even a merged/closed PR. Match both refs and the head repository.
        for (var page = 1; ; page++)
        {
            var pulls = await SendAsync(client, settings, HttpMethod.Get,
                $"{repo}/pulls?state=all&limit=50&page={page}", null, cancellationToken) as JsonArray
                ?? throw new InvalidOperationException("Gitea 返回了无效的 PR 列表。");
            foreach (var pull in pulls)
            {
                if (pull?["head"]?["ref"]?.GetValue<string>() == request.BranchName
                    && pull["base"]?["ref"]?.GetValue<string>() == request.BaseBranch
                    && string.Equals(pull["head"]?["repo"]?["full_name"]?.GetValue<string>(),
                        $"{request.Owner}/{request.Repository}", StringComparison.OrdinalIgnoreCase))
                {
                    return Result(pull, "PR 已存在，复用现有 PR。");
                }
            }

            if (pulls.Count == 0)
            {
                break;
            }
        }

        var labels = await ResolveLabelsAsync(client, settings, repo, request.Labels, cancellationToken);
        var branchPath = string.Join('/', request.BranchName.Split('/').Select(Uri.EscapeDataString));
        var branch = await SendAsync(client, settings, HttpMethod.Get,
            $"{repo}/branches/{branchPath}", null, cancellationToken, allowNotFound: true);
        if (branch is null)
        {
            var files = new JsonArray();
            foreach (var file in request.Files)
            {
                var operation = new JsonObject
                {
                    ["path"] = file.Path,
                    ["operation"] = file.Kind switch
                    {
                        FileChangeKind.Added => "create",
                        FileChangeKind.Modified => "update",
                        FileChangeKind.Deleted => "delete",
                        _ => throw new InvalidOperationException("不支持的文件变更类型。"),
                    },
                };
                if (file.Kind != FileChangeKind.Added)
                {
                    var path = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
                    var existing = await SendAsync(client, settings, HttpMethod.Get,
                        $"{repo}/contents/{path}?ref={Uri.EscapeDataString(request.BaseBranch)}", null, cancellationToken);
                    operation["sha"] = existing?["sha"]?.GetValue<string>()
                        ?? throw new InvalidOperationException($"无法读取文件 SHA：{file.Path}");
                }

                if (file.Kind != FileChangeKind.Deleted)
                {
                    operation["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content!));
                }

                files.Add(operation);
            }

            var body = new JsonObject
            {
                ["branch"] = request.BaseBranch,
                ["new_branch"] = request.BranchName,
                ["message"] = request.CommitMessage,
                ["files"] = files,
            };
            if (!string.IsNullOrWhiteSpace(settings.AuthorName) && !string.IsNullOrWhiteSpace(settings.AuthorEmail))
            {
                body["author"] = new JsonObject { ["name"] = settings.AuthorName, ["email"] = settings.AuthorEmail };
            }

            await SendAsync(client, settings, HttpMethod.Post, $"{repo}/contents", body, cancellationToken);
        }

        var title = request.Draft && !request.Title.StartsWith("WIP:", StringComparison.OrdinalIgnoreCase)
            && !request.Title.StartsWith("[WIP]", StringComparison.OrdinalIgnoreCase)
                ? $"WIP: {request.Title}" : request.Title;
        var created = await SendAsync(client, settings, HttpMethod.Post, $"{repo}/pulls", new JsonObject
        {
            ["head"] = request.BranchName,
            ["base"] = request.BaseBranch,
            ["title"] = title,
            ["body"] = request.Body,
            ["labels"] = labels,
        }, cancellationToken);
        return Result(created, "PR 已创建。");
    }

    public async Task<ProviderCheckResult> CheckAsync(PullRequestProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            if (IsDryRun(profile) && string.IsNullOrWhiteSpace(profile.GetConfig("token")))
            {
                return new ProviderCheckResult(true, "dry-run 已开启，未验证 Gitea 连接；关闭前请配置 API 地址和 Token。");
            }

            var settings = ReadSettings(profile);
            using var client = httpClientFactory.CreateClient(ProviderId);
            var user = await SendAsync(client, settings, HttpMethod.Get, "user", null, cancellationToken);
            return new ProviderCheckResult(true, $"认证成功，Gitea 用户：{user?["login"]?.GetValue<string>() ?? "unknown"}",
                $"API：{settings.ApiBaseUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderCheckResult(false, "Gitea 连接失败。", ex.Message);
        }
    }

    private static async Task<JsonArray> ResolveLabelsAsync(HttpClient client, GiteaSettings settings,
        string repo, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var remaining = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new JsonArray();
        for (var page = 1; remaining.Count > 0; page++)
        {
            var labels = await SendAsync(client, settings, HttpMethod.Get,
                $"{repo}/labels?limit=50&page={page}", null, cancellationToken) as JsonArray
                ?? throw new InvalidOperationException("Gitea 返回了无效的标签列表。");
            if (labels.Count == 0)
            {
                throw new InvalidOperationException($"Gitea 仓库标签不存在：{string.Join(", ", remaining)}");
            }

            foreach (var label in labels)
            {
                if (label?["name"]?.GetValue<string>() is { } name && remaining.Remove(name))
                {
                    result.Add(label["id"]?.GetValue<long>()
                        ?? throw new InvalidOperationException($"Gitea 标签缺少 ID：{name}"));
                }
            }
        }

        return result;
    }

    private static async Task<JsonNode?> SendAsync(HttpClient client, GiteaSettings settings,
        HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(settings.ApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("token", settings.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("dsh-superagent/1.0");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // Do not persist remote error bodies, which may echo tokens or repository content.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gitea API HTTP {(int)response.StatusCode}（{method} {request.RequestUri!.AbsolutePath}）。");
        }

        var content = await response.Content.ReadAsStringAsync(timeout.Token);
        return string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content);
    }

    private static PullRequestResult Result(JsonNode? pull, string message)
    {
        var url = pull?["html_url"]?.GetValue<string>();
        var number = pull?["number"]?.GetValue<long>();
        if (string.IsNullOrWhiteSpace(url) || number is null)
        {
            throw new InvalidOperationException("Gitea PR 响应缺少 URL 或编号。");
        }

        return new PullRequestResult(true, url, number.Value.ToString(), message);
    }

    private static bool IsDryRun(PullRequestProfile profile) =>
        !bool.TryParse(profile.GetConfig("dryRun"), out var dryRun) || dryRun;

    private static GiteaSettings ReadSettings(PullRequestProfile profile)
    {
        var url = profile.GetConfig("apiBaseUrl")?.Trim();
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException("Gitea API 地址必须是完整 HTTPS API 根地址（本机回环允许 HTTP），且不包含凭据、查询或片段。");
        }

        var token = profile.GetConfig("token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("未配置 Gitea Token。");
        }

        return new GiteaSettings(token, uri, profile.GetConfig("authorName") ?? "dsh-superagent",
            profile.GetConfig("authorEmail") ?? "dsh-superagent@localhost");
    }

    private static void Validate(PullRequestRequest request)
    {
        static bool Segment(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100
            && value is not "." and not ".."
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
        static bool Branch(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200
            && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal)
            && value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith('.')
                && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                && part.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'));

        if (!Segment(request.Owner) || !Segment(request.Repository))
        {
            throw new InvalidOperationException("Gitea owner/repository 不合法。");
        }

        if (!Branch(request.BaseBranch) || !Branch(request.BranchName) || request.BaseBranch == request.BranchName)
        {
            throw new InvalidOperationException("基线或 PR 分支名不合法，且两者不能相同。");
        }

        if (request.Files.Count is < 1 or > 30)
        {
            throw new InvalidOperationException("单次 PR 的文件数量必须在 1-30 之间。");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') || file.Path.Any(char.IsControl)
                || file.Path.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                    || part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                || !paths.Add(file.Path) || !Enum.IsDefined(file.Kind)
                || (file.Kind != FileChangeKind.Deleted && file.Content is null))
            {
                throw new InvalidOperationException($"文件路径、内容或变更类型不合法：{file.Path}");
            }
        }
    }

    private sealed record GiteaSettings(string Token, Uri ApiBaseUrl, string AuthorName, string AuthorEmail);
}
