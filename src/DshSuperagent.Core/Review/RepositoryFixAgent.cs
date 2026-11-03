using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Repositories;

namespace DshSuperagent.Core.Review;

public sealed record PatchProposal(string Diff, string Explanation, string Risk);

public sealed class RepositoryFixAgent
{
    private readonly ILlmProvider? _provider;
    private readonly LlmProfile? _profile;

    private readonly TeamPrompts? _prompts;

    public RepositoryFixAgent(ILlmProvider? provider, LlmProfile? profile, TeamPrompts? prompts = null)
    {
        _provider = provider;
        _profile = profile;
        _prompts = prompts;
    }

    public async Task<PatchProposal> ProposeAsync(
        RepositoryProfile repository,
        ReviewFinding finding,
        CancellationToken cancellationToken)
    {
        if (_provider is null || _profile is null)
        {
            throw new InvalidOperationException("未配置默认模型，无法生成修复补丁。本地静态审查仍可发现并记录问题。");
        }

        var workspace = new RepositoryWorkspace(repository.LocalPath, repository.IncludeGlobs, repository.ExcludeGlobs);
        var startLine = Math.Max(1, finding.Line - 40);
        var slice = await workspace.ReadFileAsync(finding.FilePath, startLine, 140, cancellationToken);
        var prompt = new StringBuilder();
        prompt.AppendLine("你要为下面的代码问题生成一个最小、可审查的修复补丁。");
        prompt.AppendLine("只输出一个 JSON 对象，不要 Markdown 代码围栏，字段为 diff、explanation、risk。");
        prompt.AppendLine("diff 必须是标准 unified diff，使用 a/ 和 b/ 前缀，例如：");
        prompt.AppendLine("--- a/path/file.cs");
        prompt.AppendLine("+++ b/path/file.cs");
        prompt.AppendLine("@@ -10,3 +10,3 @@");
        prompt.AppendLine("不要修改与问题无关的文件；不要引入新依赖；不要改动测试期望来掩盖问题。");
        prompt.AppendLine();
        prompt.AppendLine($"仓库：{repository.Slug}（{repository.DisplayName}）");
        prompt.AppendLine($"问题规则：{finding.RuleId} / {finding.Title}（{finding.Severity}）");
        prompt.AppendLine($"文件：{finding.FilePath}:{finding.Line}");
        prompt.AppendLine($"证据：\n{finding.Evidence}");
        if (!string.IsNullOrWhiteSpace(repository.Instructions))
        {
            prompt.AppendLine($"团队约定（背景）：{repository.Instructions}");
        }

        if (_prompts is not null)
        {
            prompt.AppendLine($"沟通要求：{_prompts.Communication}");
            prompt.AppendLine($"工单范围：{_prompts.TicketScope}");
            prompt.AppendLine($"交付要求：{_prompts.DeliveryRequirements}");
        }

        prompt.AppendLine("相关代码：");
        prompt.AppendLine("```");
        prompt.AppendLine(slice.Content);
        prompt.AppendLine("```");

        var response = await _provider.ChatAsync(
            _profile,
            new LlmChatRequest(
                [
                    new LlmMessage("system", "你是谨慎的高级工程师。修复必须最小化，保持现有代码风格，并保留原有行为，除非修复本身要求改变。"),
                    new LlmMessage("user", prompt.ToString()),
                ],
                null,
                0.1,
                4096),
            cancellationToken);

        var content = response.Content ?? string.Empty;
        var json = ExtractJson(content);
        if (json is null)
        {
            throw new InvalidOperationException("模型没有返回可解析的 JSON 补丁。");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var diff = root.TryGetProperty("diff", out var diffElement) ? diffElement.GetString() : null;
        var explanation = root.TryGetProperty("explanation", out var explanationElement) ? explanationElement.GetString() : null;
        var risk = root.TryGetProperty("risk", out var riskElement) ? riskElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(diff) || (!diff.Contains("--- ", StringComparison.Ordinal) && !diff.Contains("diff --git", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("模型返回的 diff 不是标准 unified diff。");
        }

        return new PatchProposal(diff.Trim() + "\n", explanation?.Trim() ?? "未提供说明。", risk?.Trim() ?? "未提供风险评估。");
    }

    private static string? ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return trimmed[start..(end + 1)];
    }
}

public sealed record FixOutcome(
    bool Success,
    FixStatus Status,
    string? Patch,
    string? Explanation,
    string? BranchName,
    string? PullRequestUrl,
    string? Error,
    string? ValidationOutput);

public sealed class AutonomousFixService
{
    private readonly AgentSettings _settings;

    public AutonomousFixService(AgentSettings settings)
    {
        _settings = settings;
    }

    public async Task<FixOutcome> RunAsync(
        RepositoryProfile repository,
        ReviewFinding finding,
        ILlmProvider? llmProvider,
        LlmProfile? llmProfile,
        IPullRequestProvider? pullRequestProvider,
        PullRequestProfile? pullRequestProfile,
        CancellationToken cancellationToken,
        TeamPrompts? prompts = null)
    {
        PatchProposal proposal;
        try
        {
            var agent = new RepositoryFixAgent(llmProvider, llmProfile, prompts);
            proposal = await agent.ProposeAsync(repository, finding, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FixOutcome(false, FixStatus.Failed, null, null, null, null, ex.Message, null);
        }

        var validator = new GitPatchValidator(_settings);
        var validation = await validator.ValidateAsync(repository, proposal.Diff, cancellationToken);
        if (!validation.Ok)
        {
            return new FixOutcome(false, FixStatus.Failed, proposal.Diff, proposal.Explanation, null, null, validation.Error, validation.Output);
        }

        var baseCommit = await ReadBaseCommitAsync(repository, cancellationToken);
        var ruleName = Sanitize(finding.RuleId);
        if (ruleName.Length > 40)
        {
            ruleName = ruleName[..40];
        }

        var branch = $"superagent/fix-{ruleName}-{StableHash($"{repository.Slug}|{finding.Signature()}|{baseCommit}")}";
        if (pullRequestProvider is null || pullRequestProfile is null)
        {
            return new FixOutcome(
                true,
                FixStatus.PatchReady,
                proposal.Diff,
                proposal.Explanation,
                branch,
                null,
                "未配置 PR Provider，补丁已保存，等待人工应用。",
                validation.Output);
        }

        string owner;
        string repoName;
        try
        {
            (owner, repoName) = ResolveTarget(repository);
        }
        catch (InvalidOperationException ex)
        {
            return new FixOutcome(true, FixStatus.PatchReady, proposal.Diff, proposal.Explanation, branch, null, ex.Message, validation.Output);
        }

        var dryRun = !bool.TryParse(pullRequestProfile.GetConfig("dryRun"), out var configuredDryRun) || configuredDryRun;
        if (dryRun)
        {
            return new FixOutcome(
                true,
                FixStatus.PatchReady,
                proposal.Diff,
                proposal.Explanation,
                branch,
                null,
                "PR Provider 处于 dry-run，补丁已保存，未创建分支或 PR。",
                validation.Output);
        }

        var body = BuildPullRequestBody(finding, proposal, validation.Output);
        var labels = (pullRequestProfile.GetConfig("labels") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var request = new PullRequestRequest(
            owner,
            repoName,
            repository.DefaultBranch,
            branch,
            $"fix: {finding.Title}（{finding.FilePath}:{finding.Line}）",
            body,
            $"fix: {finding.Title}\n\n由 dsh-superagent 自动审查生成。",
            validation.Files,
            bool.TryParse(pullRequestProfile.GetConfig("draft"), out var draft) && draft,
            labels);

        try
        {
            var result = await pullRequestProvider.CreateAsync(pullRequestProfile, request, cancellationToken);
            if (!result.Created)
            {
                return new FixOutcome(
                    true,
                    FixStatus.PatchReady,
                    proposal.Diff,
                    proposal.Explanation,
                    branch,
                    null,
                    result.Message ?? "PR Provider 未创建 PR。",
                    validation.Output);
            }

            return new FixOutcome(
                true,
                FixStatus.PullRequestOpen,
                proposal.Diff,
                proposal.Explanation,
                branch,
                result.Url,
                null,
                validation.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FixOutcome(false, FixStatus.Failed, proposal.Diff, proposal.Explanation, branch, null, ex.Message, validation.Output);
        }
    }

    public static (string Owner, string Repository) ResolveTarget(RepositoryProfile repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.PullRequestOwner) && !string.IsNullOrWhiteSpace(repository.PullRequestRepository))
        {
            return (repository.PullRequestOwner.Trim(), repository.PullRequestRepository.Trim());
        }

        var remote = repository.RemoteUrl?.Trim();
        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new InvalidOperationException("仓库未配置 RemoteUrl 或 PR Owner/Repository，无法创建 PR。");
        }

        var match = Regex.Match(remote, @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException("无法从 RemoteUrl 识别 GitHub owner/repository，请显式配置 PR Owner/Repository。");
        }

        return (match.Groups["owner"].Value, match.Groups["repo"].Value);
    }

    private static string BuildPullRequestBody(ReviewFinding finding, PatchProposal proposal, string validation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 自动审查发现");
        builder.AppendLine();
        builder.AppendLine($"- 规则：`{finding.RuleId}` / {finding.Title}");
        builder.AppendLine($"- 严重度：{finding.Severity}");
        builder.AppendLine($"- 位置：`{finding.FilePath}:{finding.Line}`");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(finding.Evidence);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## 修复说明");
        builder.AppendLine();
        builder.AppendLine(proposal.Explanation);
        builder.AppendLine();
        builder.AppendLine("## 风险");
        builder.AppendLine();
        builder.AppendLine(proposal.Risk);
        builder.AppendLine();
        builder.AppendLine("## 验证");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(validation.Length > 4000 ? validation[..4000] + "\n…" : validation);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("> 本 PR 由 dsh-superagent 在独立 worktree 中生成并验证。请人工复核后合并。");
        return builder.ToString();
    }

    private static async Task<string> ReadBaseCommitAsync(RepositoryProfile repository, CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(repository.DefaultBranch)
            ? "HEAD"
            : $"refs/heads/{repository.DefaultBranch}";
        var result = await ProcessRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", reference],
            repository.LocalPath,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.ExitCode == 0)
        {
            return result.Output.Trim();
        }

        var head = await ProcessRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", "HEAD"],
            repository.LocalPath,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        return head.ExitCode == 0 ? head.Output.Trim() : "no-commit";
    }

    private static string Sanitize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
