using System.Text.RegularExpressions;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Repositories;

namespace DshSuperagent.Core.Review;

public sealed record ReviewScanResult(IReadOnlyList<ReviewFinding> Findings, int FilesScanned);

public sealed class StaticCodeReviewer
{
    // Deterministic Source adapter; findings become immutable Triggers.
    private static readonly (string Id, FindingSeverity Severity, string Title, Regex Pattern, string Advice)[] Rules =
    [
        ("hardcoded-secret", FindingSeverity.High, "疑似硬编码凭据",
            new Regex("(?i)(password|passwd|secret|api[_-]?key|access[_-]?token)\\s*[:=]\\s*[\"'][^\"']{8,}[\"']", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "把凭据移到环境变量或密钥管理服务，并轮换已经提交的凭据。"),
        ("not-implemented", FindingSeverity.High, "生产代码抛出 NotImplementedException",
            new Regex("throw\\s+new\\s+NotImplementedException", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "实现该分支，或改成明确的业务异常并补测试。"),
        ("empty-catch", FindingSeverity.Warning, "空的 catch 块吞掉异常",
            new Regex("^\\s*catch\\s*(\\([^)]*\\))?\\s*\\{\\s*\\}\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "至少记录异常并说明为何可以继续，或向上抛出。"),
        ("async-void", FindingSeverity.Warning, "async void 方法无法被等待",
            new Regex("\\basync\\s+void\\s+\\w+\\s*\\(", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "除事件处理器外改为 async Task，并让调用方 await。"),
        ("blocking-task", FindingSeverity.Warning, "同步阻塞 Task",
            new Regex("\\.(Result|Wait\\(\\))", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "改为 await，避免线程池饥饿和死锁。"),
        ("todo-marker", FindingSeverity.Info, "未完成的 TODO/FIXME",
            new Regex("\\b(TODO|FIXME|HACK)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "补充任务单号或完成该处工作，避免长期遗留。"),
    ];

    public async Task<ReviewScanResult> ScanAsync(
        RepositoryProfile repository,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var workspace = new RepositoryWorkspace(repository.LocalPath, repository.IncludeGlobs, repository.ExcludeGlobs);
        if (!workspace.Exists)
        {
            throw new InvalidOperationException($"代码仓库目录不存在：{repository.LocalPath}");
        }

        var files = await workspace.ListFilesAsync(null, Math.Clamp(maxFiles, 1, 2_000), cancellationToken);
        var findings = new List<ReviewFinding>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = await workspace.ReadFileAsync(file.Path, 1, 250, cancellationToken);
            if (slice.TotalLines < 1)
            {
                continue;
            }

            var lines = slice.Content.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(line))
                    {
                        continue;
                    }

                    if (rule.Id == "hardcoded-secret" && IsPlaceholder(line))
                    {
                        continue;
                    }

                    var evidenceStart = Math.Max(0, index - 1);
                    var evidenceEnd = Math.Min(lines.Length - 1, index + 1);
                    var evidence = string.Join('\n', lines[evidenceStart..(evidenceEnd + 1)]).Trim();
                    findings.Add(new ReviewFinding
                    {
                        RuleId = rule.Id,
                        Severity = rule.Severity,
                        FilePath = file.Path,
                        Line = slice.StartLine + index,
                        Title = rule.Title,
                        Evidence = evidence,
                        Explanation = rule.Advice,
                    });

                    if (findings.Count >= 200)
                    {
                        return new ReviewScanResult(findings, files.Count);
                    }
                }
            }

            if (files.Count > 0 && slice.TotalLines > 250)
            {
                // Scan the rest of large files in bounded windows.
                var start = 251;
                while (start <= slice.TotalLines)
                {
                    var window = await workspace.ReadFileAsync(file.Path, start, 250, cancellationToken);
                    var windowLines = window.Content.Split('\n');
                    for (var index = 0; index < windowLines.Length; index++)
                    {
                        foreach (var rule in Rules)
                        {
                            if (!rule.Pattern.IsMatch(windowLines[index]))
                            {
                                continue;
                            }

                            if (rule.Id == "hardcoded-secret" && IsPlaceholder(windowLines[index]))
                            {
                                continue;
                            }

                            findings.Add(new ReviewFinding
                            {
                                RuleId = rule.Id,
                                Severity = rule.Severity,
                                FilePath = file.Path,
                                Line = window.StartLine + index,
                                Title = rule.Title,
                                Evidence = windowLines[index].Trim(),
                                Explanation = rule.Advice,
                            });
                            if (findings.Count >= 200)
                            {
                                return new ReviewScanResult(findings, files.Count);
                            }
                        }
                    }

                    if (!window.Truncated)
                    {
                        break;
                    }

                    start += 250;
                }
            }
        }

        return new ReviewScanResult(findings, files.Count);
    }

    private static bool IsPlaceholder(string line)
    {
        var lowered = line.ToLowerInvariant();
        return lowered.Contains("example", StringComparison.Ordinal)
            || lowered.Contains("changeme", StringComparison.Ordinal)
            || lowered.Contains("your-", StringComparison.Ordinal)
            || lowered.Contains("your_", StringComparison.Ordinal)
            || lowered.Contains("placeholder", StringComparison.Ordinal)
            || lowered.Contains("dummy", StringComparison.Ordinal)
            || lowered.Contains("xxxx", StringComparison.Ordinal)
            || lowered.Contains("${", StringComparison.Ordinal)
            || lowered.Contains("{{", StringComparison.Ordinal);
    }
}
