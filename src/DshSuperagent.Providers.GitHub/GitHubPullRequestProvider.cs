using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Providers.PullRequests;

namespace DshSuperagent.Providers.GitHub;

public sealed class GitHubPullRequestProvider : IPullRequestProvider
{
    public const string ProviderId = "github";

    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubPullRequestProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Id => ProviderId;
    public string DisplayName => "GitHub Pull Request";
    public string Description => "通过 GitHub Git Data API 创建分支、提交和 Pull Request，无需本地推送凭据。";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("token", "GitHub Token", ProviderFieldType.Secret,
            Help: "需要 repo 权限的细粒度 PAT。dry-run 开启时可暂不填写。"),
        new("apiBaseUrl", "API 地址", ProviderFieldType.Text, DefaultValue: "https://api.github.com",
            Help: "GitHub.com 保持默认；GitHub Enterprise 填 https://host/api/v3。"),
        new("dryRun", "Dry-run（不创建分支/PR）", ProviderFieldType.Bool, DefaultValue: "true",
            Help: "默认开启。关闭后才会真正推送分支并创建 PR。"),
        new("draft", "创建草稿 PR", ProviderFieldType.Bool, DefaultValue: "false"),
        new("labels", "PR 标签", ProviderFieldType.Text,
            Help: "可选，多个标签用逗号分隔。"),
        new("authorName", "提交作者名称", ProviderFieldType.Text, DefaultValue: "dsh-superagent"),
        new("authorEmail", "提交作者邮箱", ProviderFieldType.Text, DefaultValue: "dsh-superagent@users.noreply.github.com"),
    ];

    public async Task<PullRequestResult> CreateAsync(
        PullRequestProfile profile,
        PullRequestRequest request,
        CancellationToken cancellationToken)
    {
        var settings = ReadSettings(profile);
        Validate(request);
        if (settings.DryRun)
        {
            return new PullRequestResult(false, null, null, "dry-run 已开启，未创建分支或 PR。");
        }

        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            throw new InvalidOperationException("未配置 GitHub Token，无法创建 PR。");
        }

        var client = CreateClient(settings);
        var baseRef = await SendAsync(
            client,
            settings,
            HttpMethod.Get,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/ref/heads/{EscapeRef(request.BaseBranch)}",
            null,
            cancellationToken);
        var baseCommitSha = baseRef["object"]?["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"无法读取基线分支 {request.BaseBranch}。");

        var baseCommit = await SendAsync(
            client,
            settings,
            HttpMethod.Get,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/commits/{baseCommitSha}",
            null,
            cancellationToken);
        var baseTreeSha = baseCommit["tree"]?["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException("无法读取基线 commit 的 tree。");

        var treeEntries = new JsonArray();
        foreach (var file in request.Files)
        {
            if (file.Kind == FileChangeKind.Deleted)
            {
                treeEntries.Add(new JsonObject
                {
                    ["path"] = file.Path,
                    ["mode"] = "100644",
                    ["type"] = "blob",
                    ["sha"] = null,
                });
                continue;
            }

            if (file.Content is null)
            {
                throw new InvalidOperationException($"文件 {file.Path} 缺少内容。");
            }

            var blob = await SendAsync(
                client,
                settings,
                HttpMethod.Post,
                $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/blobs",
                new JsonObject
                {
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content)),
                    ["encoding"] = "base64",
                },
                cancellationToken);
            var blobSha = blob["sha"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"创建 blob 失败：{file.Path}");
            treeEntries.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["mode"] = "100644",
                ["type"] = "blob",
                ["sha"] = blobSha,
            });
        }

        var tree = await SendAsync(
            client,
            settings,
            HttpMethod.Post,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/trees",
            new JsonObject
            {
                ["base_tree"] = baseTreeSha,
                ["tree"] = treeEntries,
            },
            cancellationToken);
        var treeSha = tree["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException("创建 tree 失败。");

        var commitBody = new JsonObject
        {
            ["message"] = request.CommitMessage,
            ["tree"] = treeSha,
            ["parents"] = new JsonArray(baseCommitSha),
        };
        if (!string.IsNullOrWhiteSpace(settings.AuthorName) && !string.IsNullOrWhiteSpace(settings.AuthorEmail))
        {
            commitBody["author"] = new JsonObject
            {
                ["name"] = settings.AuthorName,
                ["email"] = settings.AuthorEmail,
            };
        }

        var commit = await SendAsync(
            client,
            settings,
            HttpMethod.Post,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/commits",
            commitBody,
            cancellationToken);
        var commitSha = commit["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException("创建 commit 失败。");

        var branchRefPath = $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/ref/heads/{EscapeRef(request.BranchName)}";
        var existingBranch = await TrySendAsync(client, settings, HttpMethod.Get, branchRefPath, null, cancellationToken);
        if (existingBranch is null)
        {
            await SendAsync(
                client,
                settings,
                HttpMethod.Post,
                $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/git/refs",
                new JsonObject
                {
                    ["ref"] = $"refs/heads/{request.BranchName}",
                    ["sha"] = commitSha,
                },
                cancellationToken);
        }

        var existingPulls = await SendAsync(
            client,
            settings,
            HttpMethod.Get,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/pulls?head={Uri.EscapeDataString($"{request.Owner}:{request.BranchName}")}&state=all",
            null,
            cancellationToken);
        if (existingPulls is JsonArray { Count: > 0 } pulls)
        {
            var existingPull = pulls[0];
            return new PullRequestResult(
                true,
                existingPull?["html_url"]?.GetValue<string>(),
                existingPull?["number"]?.GetValue<int>().ToString(),
                "PR 已存在，复用现有 PR。",
                null);
        }

        var pr = await SendAsync(
            client,
            settings,
            HttpMethod.Post,
            $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/pulls",
            new JsonObject
            {
                ["title"] = request.Title,
                ["head"] = request.BranchName,
                ["base"] = request.BaseBranch,
                ["body"] = request.Body,
                ["draft"] = request.Draft,
            },
            cancellationToken);
        var url = pr["html_url"]?.GetValue<string>();
        var number = pr["number"]?.GetValue<int>();
        var labelMessage = string.Empty;
        if (request.Labels.Count > 0 && number is { } prNumber)
        {
            try
            {
                await SendAsync(
                    client,
                    settings,
                    HttpMethod.Post,
                    $"repos/{EscapeOwner(request.Owner)}/{EscapeOwner(request.Repository)}/issues/{prNumber}/labels",
                    new JsonObject { ["labels"] = new JsonArray(request.Labels.Select(label => JsonValue.Create(label)).ToArray()) },
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                labelMessage = "（PR 已创建，但标签添加失败）";
            }
        }

        return new PullRequestResult(
            true,
            url,
            number?.ToString(),
            $"PR 已创建{labelMessage}",
            null);
    }

    public async Task<ProviderCheckResult> CheckAsync(PullRequestProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var settings = ReadSettings(profile);
            if (settings.DryRun && string.IsNullOrWhiteSpace(settings.Token))
            {
                return new ProviderCheckResult(true, "dry-run 已开启，无需 GitHub Token。关闭 dry-run 前请先配置 Token。");
            }

            if (string.IsNullOrWhiteSpace(settings.Token))
            {
                return new ProviderCheckResult(false, "未配置 GitHub Token。");
            }

            var client = CreateClient(settings);
            var user = await SendAsync(client, settings, HttpMethod.Get, "user", null, cancellationToken);
            var login = user["login"]?.GetValue<string>() ?? "unknown";
            return new ProviderCheckResult(true, $"认证成功，GitHub 用户：{login}", $"API：{settings.ApiBaseUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderCheckResult(false, "GitHub 连接失败。", ex.Message);
        }
    }

    private HttpClient CreateClient(GitHubSettings settings)
    {
        var client = _httpClientFactory.CreateClient("github");
        return client;
    }

    private static async Task<JsonNode?> TrySendAsync(
        HttpClient client,
        GitHubSettings settings,
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(client, settings, method, relativePath, body, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static async Task<JsonNode> SendAsync(
        HttpClient client,
        GitHubSettings settings,
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(settings.ApiBaseUrl, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsh-superagent/1.0");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        var content = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var message = ExtractError(content);
            throw new InvalidOperationException($"GitHub API HTTP {(int)response.StatusCode}：{message}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(content) ?? new JsonObject();
    }

    private static void Validate(PullRequestRequest request)
    {
        if (!IsValidSegment(request.Owner) || !IsValidSegment(request.Repository))
        {
            throw new InvalidOperationException("GitHub owner/repository 不合法。");
        }

        if (string.IsNullOrWhiteSpace(request.BaseBranch) || request.BaseBranch.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("基线分支不合法。");
        }

        if (string.IsNullOrWhiteSpace(request.BranchName)
            || request.BranchName.Length > 200
            || request.BranchName.Contains("..", StringComparison.Ordinal)
            || request.BranchName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/')))
        {
            throw new InvalidOperationException("PR 分支名不合法。");
        }

        if (request.Files.Count == 0 || request.Files.Count > 30)
        {
            throw new InvalidOperationException("单次 PR 的文件数量必须在 1-30 之间。");
        }

        foreach (var file in request.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)
                || file.Path.StartsWith('/')
                || file.Path.Contains("..", StringComparison.Ordinal)
                || file.Path.Contains('\\', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"文件路径不合法：{file.Path}");
            }
        }
    }

    private static GitHubSettings ReadSettings(PullRequestProfile profile)
    {
        var baseUrlText = string.IsNullOrWhiteSpace(profile.GetConfig("apiBaseUrl"))
            ? "https://api.github.com"
            : profile.GetConfig("apiBaseUrl")!;
        if (!Uri.TryCreate(baseUrlText.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps
                && !(baseUri.Scheme == Uri.UriSchemeHttp && (baseUri.IsLoopback || baseUri.Host == "localhost"))))
        {
            throw new InvalidOperationException("GitHub API 地址必须是 HTTPS（本地测试允许 HTTP 回环地址）。");
        }

        return new GitHubSettings(
            profile.GetConfig("token") ?? string.Empty,
            baseUri,
            !bool.TryParse(profile.GetConfig("dryRun"), out var dryRun) || dryRun,
            bool.TryParse(profile.GetConfig("draft"), out var draft) && draft,
            profile.GetConfig("authorName") ?? "dsh-superagent",
            profile.GetConfig("authorEmail") ?? "dsh-superagent@users.noreply.github.com");
    }

    private static string EscapeOwner(string value) => Uri.EscapeDataString(value);

    private static string EscapeRef(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static bool IsValidSegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string ExtractError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return Trim(message.GetString() ?? content, 500);
            }

            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                return Trim(errors.ToString(), 500);
            }
        }
        catch (JsonException)
        {
        }

        return Trim(content, 500);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record GitHubSettings(
        string Token,
        Uri ApiBaseUrl,
        bool DryRun,
        bool Draft,
        string AuthorName,
        string AuthorEmail);
}
