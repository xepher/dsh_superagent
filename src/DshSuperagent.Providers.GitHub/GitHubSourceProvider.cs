using System.Text.Json;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.Common;

namespace DshSuperagent.Providers.GitHub;

public sealed class GitHubSourceProvider(IProviderCommandRunner commands) : CodeHostSourceProvider
{
    public override string Id => "github";
    public override string DisplayName => "GitHub（原版 CLI 来源）";
    public override string Description => "使用指定账号的 gh 轮询仓库通知、Issue/PR 讨论、CI 与 PR 合并关闭，并幂等回复；不是仅创建 PR。";
    protected override bool Github => true;
    protected override string WebBase(Source source) => "https://github.com";
    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("cli", "智能体 GitHub CLI", ProviderFieldType.Text, Required: true, DefaultValue: "gh",
            Help: "可执行文件名或绝对路径；用独立 GH_CONFIG_DIR 登录智能体账号，不是命令行字符串。"),
        new("account", "预期智能体账号 login", ProviderFieldType.Text, Required: true,
            Help: "实际 gh 账号必须匹配，避免误用个人账号。"),
        new("configDir", "独立 GH_CONFIG_DIR", ProviderFieldType.Text,
            Help: "可选的绝对配置目录；事先在相同目录运行 gh auth login。忽略继承的 GH_TOKEN/GITHUB_TOKEN。"),
        .. PollFields,
    ];

    protected override async Task<CodeHostResponse> ApiAsync(Source source, string method, string path, object? body, CancellationToken token)
    {
        var expected = source.Required("account");
        var environment = new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null };
        if (source.GetConfig("configDir") is { Length: > 0 } directory)
        {
            if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("GH_CONFIG_DIR 必须是绝对路径。");
            environment["GH_CONFIG_DIR"] = directory;
        }
        var arguments = new List<string> { "api", path, "--hostname", "github.com", "--method", method, "--include", "-H", "Accept: application/vnd.github+json",
            "-H", "X-GitHub-Api-Version: 2022-11-28" };
        if (body is not null) arguments.AddRange(["--input", "-"]);
        string raw;
        try
        {
            raw = await commands.RunAsync(new(source.Required("cli"), arguments,
                body is null ? null : JsonSerializer.Serialize(body), environment), token);
        }
        catch (ProviderCommandException ex) when (ex.ApiCode is { } status)
        {
            throw new CodeHostException(status, ex.Locked);
        }
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var width = 4;
        if (split < 0) { split = raw.IndexOf("\n\n", StringComparison.Ordinal); width = 2; }
        if (split < 0) throw new InvalidOperationException("GitHub CLI 未返回 HTTP 响应头。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw[..split].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var json = raw[(split + width)..].Trim();
        var data = SourceJson.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
        if (path == "user" && !string.Equals(data.String("login"), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("实际 GitHub CLI 账号与预期智能体账号不匹配。");
        return new CodeHostResponse(data, headers);
    }
}
