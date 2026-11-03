using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Agent;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Review;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Core.Application;

public sealed class RepositoryQuestionExecutor(
    IConfigurationStore configuration,
    LlmProviderRegistry llms) : ITicketExecutor
{
    public WorkKind Kind => WorkKind.Answer;
    public string Name => "repository-question";

    public async Task<ExecutorReport> ExecuteAsync(TicketExecutionContext context, CancellationToken cancellationToken)
    {
        var llm = await configuration.GetDefaultLlmAsync(cancellationToken);
        var provider = llm is null ? null : llms.Find(llm.ProviderId);
        var settings = await configuration.GetAgentSettingsAsync(cancellationToken);
        var question = context.Inputs
            .OrderByDescending(input => input.CreatedAt)
            .Select(input => input.Content)
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content))
            ?? context.OriginTrigger?.Text
            ?? string.Empty;
        var agent = new RepositoryQuestionAgent(provider, llm, settings);
        var answer = await agent.AnswerAsync(context.Repository, question, cancellationToken, context.Prompts);
        return new ExecutorReport(
            ReportKind.Ready,
            answer.Markdown,
            ChannelContent: answer.Markdown,
            RawJson: answer.ToolTraceJson);
    }
}

public sealed class RepositoryFixExecutor(
    IConfigurationStore configuration,
    LlmProviderRegistry llms,
    PullRequestProviderRegistry pullRequests) : ITicketExecutor
{
    public WorkKind Kind => WorkKind.Fix;
    public string Name => "repository-fix";

    public async Task<ExecutorReport> ExecuteAsync(TicketExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = await configuration.GetAgentSettingsAsync(cancellationToken);
        var finding = ParseFinding(context.OriginTrigger?.PayloadJson);
        if (finding is null)
        {
            var reviewer = new StaticCodeReviewer();
            var scan = await reviewer.ScanAsync(context.Repository, settings.MaxReviewFiles, cancellationToken);
            finding = scan.Findings
                .Where(item => item.Severity != FindingSeverity.Info)
                .OrderByDescending(item => item.Severity)
                .FirstOrDefault();
            if (finding is null)
            {
                return new ExecutorReport(
                    ReportKind.Dropped,
                    $"在 `{context.Repository.Slug}` 中没有找到可修复的问题。",
                    ChannelContent: $"没有在 `{context.Repository.Slug}` 中找到可修复的问题。");
            }
        }

        var llm = await configuration.GetDefaultLlmAsync(cancellationToken);
        var llmProvider = llm is null ? null : llms.Find(llm.ProviderId);
        var pullRequestProfile = context.Repository.PullRequestProfileId is { } profileId
            ? await configuration.GetPullRequestProfileAsync(profileId, cancellationToken)
            : await configuration.GetDefaultPullRequestAsync(cancellationToken);
        var pullRequestProvider = pullRequestProfile is null ? null : pullRequests.Find(pullRequestProfile.ProviderId);

        var service = new AutonomousFixService(settings);
        var outcome = await service.RunAsync(
            context.Repository,
            finding,
            llmProvider,
            llm,
            pullRequestProvider,
            pullRequestProfile,
            cancellationToken,
            context.Prompts);
        var artifact = JsonSerializer.Serialize(new
        {
            finding = new
            {
                rule_id = finding.RuleId,
                severity = finding.Severity.ToString(),
                file = finding.FilePath,
                line = finding.Line,
                title = finding.Title,
                evidence = finding.Evidence,
            },
            patch = outcome.Patch,
            explanation = outcome.Explanation,
            branch = outcome.BranchName,
            validation = outcome.ValidationOutput,
            error = outcome.Error,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (outcome.Status == FixStatus.PullRequestOpen)
        {
            var message = $"已提交修复 PR：{outcome.PullRequestUrl}\n\n{outcome.Explanation}\n\n> 请在合并前复核验证结果。";
            return new ExecutorReport(
                ReportKind.Ready,
                message,
                PullRequestUrl: outcome.PullRequestUrl,
                ChannelContent: message,
                ArtifactJson: artifact);
        }

        if (outcome.Status == FixStatus.PatchReady)
        {
            var message = $"补丁已生成并保存，尚未创建 PR。{outcome.Error}\n\n{outcome.Explanation}";
            return new ExecutorReport(
                ReportKind.Ready,
                message,
                ChannelContent: message,
                ArtifactJson: artifact);
        }

        return new ExecutorReport(
            ReportKind.Failed,
            $"修复失败：{outcome.Error}",
            ChannelContent: $"修复失败：{outcome.Error}",
            ArtifactJson: artifact);
    }

    private static ReviewFinding? ParseFinding(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("finding", out var finding)
                || finding.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var ruleId = finding.TryGetProperty("rule_id", out var rule) ? rule.GetString() : null;
            var title = finding.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var path = finding.TryGetProperty("file", out var pathElement) ? pathElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var severity = finding.TryGetProperty("severity", out var severityElement)
                           && Enum.TryParse<FindingSeverity>(severityElement.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : FindingSeverity.Warning;
            var line = finding.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out var lineNumber)
                ? lineNumber
                : 1;
            var evidence = finding.TryGetProperty("evidence", out var evidenceElement) ? evidenceElement.GetString() : string.Empty;
            return new ReviewFinding
            {
                RuleId = ruleId,
                Severity = severity,
                FilePath = path,
                Line = line,
                Title = title,
                Evidence = evidence ?? string.Empty,
                Explanation = string.Empty,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
