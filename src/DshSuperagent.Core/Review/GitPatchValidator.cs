using System.Text;
using System.Text.RegularExpressions;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Repositories;

namespace DshSuperagent.Core.Review;

public sealed record PatchValidationResult(
    bool Ok,
    IReadOnlyList<PullRequestFile> Files,
    string? Error,
    string Output);

public sealed class GitPatchValidator
{
    // Patch validation happens in a throw-away Git worktree, never in the operator's worktree.
    private readonly AgentSettings _settings;

    public GitPatchValidator(AgentSettings settings)
    {
        _settings = settings;
    }

    public async Task<PatchValidationResult> ValidateAsync(
        RepositoryProfile repository,
        string patch,
        CancellationToken cancellationToken)
    {
        var workspace = new RepositoryWorkspace(repository.LocalPath, repository.IncludeGlobs, repository.ExcludeGlobs);
        if (!workspace.IsGitRepository)
        {
            return new PatchValidationResult(false, [], "仓库不是 Git 仓库，无法验证补丁。", string.Empty);
        }

        if (string.IsNullOrWhiteSpace(patch) || patch.Length > 200_000)
        {
            return new PatchValidationResult(false, [], "补丁为空或超过 200 KB 限制。", string.Empty);
        }

        var paths = ExtractPatchPaths(patch);
        if (paths.Count == 0)
        {
            return new PatchValidationResult(false, [], "补丁中没有可识别的文件路径。", string.Empty);
        }

        if (paths.Count > Math.Clamp(repository.MaxPatchFiles, 1, 20))
        {
            return new PatchValidationResult(false, [], $"补丁修改了 {paths.Count} 个文件，超过仓库限制 {repository.MaxPatchFiles}。", string.Empty);
        }

        foreach (var path in paths)
        {
            try
            {
                _ = workspace.ResolvePath(path);
            }
            catch (InvalidOperationException ex)
            {
                return new PatchValidationResult(false, [], $"补丁路径非法：{path}（{ex.Message}）", string.Empty);
            }
        }

        var baseRef = await ResolveBaseRefAsync(workspace, repository.DefaultBranch, cancellationToken);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-superagent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tempRoot)!);
        try
        {
            var add = await ProcessRunner.RunAsync(
                "git",
                ["worktree", "add", "--detach", tempRoot, baseRef],
                workspace.Root,
                TimeSpan.FromSeconds(60),
                cancellationToken);
            if (add.ExitCode != 0)
            {
                return new PatchValidationResult(false, [], $"创建临时 worktree 失败：{add.Output}", string.Empty);
            }

            var check = await ProcessRunner.RunAsync(
                "git",
                ["apply", "--check", "--whitespace=nowarn", "-"],
                tempRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                patch);
            if (check.ExitCode != 0)
            {
                return new PatchValidationResult(false, [], $"补丁无法应用：{check.Output}", string.Empty);
            }

            var apply = await ProcessRunner.RunAsync(
                "git",
                ["apply", "--whitespace=nowarn", "-"],
                tempRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                patch);
            if (apply.ExitCode != 0)
            {
                return new PatchValidationResult(false, [], $"补丁应用失败：{apply.Output}", string.Empty);
            }

            var addAll = await ProcessRunner.RunAsync("git", ["add", "-A"], tempRoot, TimeSpan.FromSeconds(30), cancellationToken);
            if (addAll.ExitCode != 0)
            {
                return new PatchValidationResult(false, [], $"暂存补丁失败：{addAll.Output}", string.Empty);
            }

            var diff = await ProcessRunner.RunAsync(
                "git",
                ["diff", "--cached", "--name-status", "HEAD"],
                tempRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (diff.ExitCode != 0)
            {
                return new PatchValidationResult(false, [], $"读取变更失败：{diff.Output}", string.Empty);
            }

            var files = new List<PullRequestFile>();
            foreach (var line in diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var kind = parts[0][0] switch
                {
                    'A' => FileChangeKind.Added,
                    'D' => FileChangeKind.Deleted,
                    'M' => FileChangeKind.Modified,
                    _ => (FileChangeKind?)null,
                };
                if (kind is null)
                {
                    continue;
                }

                var relative = parts[^1].Replace('\\', '/');
                string? content = null;
                if (kind != FileChangeKind.Deleted)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(tempRoot, relative));
                    var tempPrefix = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"补丁路径越界：{relative}");
                    }
                    var info = new FileInfo(fullPath);
                    if (!info.Exists || info.Length > 1024 * 1024)
                    {
                        return new PatchValidationResult(false, [], $"补丁产物过大或不存在：{relative}", string.Empty);
                    }

                    var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                    if (bytes.Any(value => value == 0))
                    {
                        return new PatchValidationResult(false, [], $"补丁产物包含二进制文件：{relative}", string.Empty);
                    }

                    content = Encoding.UTF8.GetString(bytes);
                }

                files.Add(new PullRequestFile(relative, kind.Value, content));
            }

            if (files.Count == 0)
            {
                return new PatchValidationResult(false, [], "补丁没有产生任何文件变更。", string.Empty);
            }

            var validationOutput = "未配置验证命令。";
            if (!string.IsNullOrWhiteSpace(repository.ValidationCommand))
            {
                var (fileName, arguments) = OperatingSystem.IsWindows()
                    ? ("cmd.exe", new[] { "/d", "/s", "/c", repository.ValidationCommand })
                    : ("/bin/sh", ["-c", repository.ValidationCommand]);
                var validation = await ProcessRunner.RunAsync(
                    fileName,
                    arguments,
                    tempRoot,
                    TimeSpan.FromSeconds(Math.Clamp(_settings.PatchValidationTimeoutSeconds, 10, 900)),
                    cancellationToken);
                validationOutput = validation.Output;
                if (validation.ExitCode != 0)
                {
                    return new PatchValidationResult(false, [], $"验证命令失败（退出码 {validation.ExitCode}）：{validation.Output}", validationOutput);
                }
            }

            return new PatchValidationResult(true, files, null, validationOutput);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PatchValidationResult(false, [], ex.Message, string.Empty);
        }
        finally
        {
            var remove = await ProcessRunner.RunAsync(
                "git",
                ["worktree", "remove", "--force", tempRoot],
                workspace.Root,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            if (remove.ExitCode != 0 && Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
            }

            _ = await ProcessRunner.RunAsync("git", ["worktree", "prune"], workspace.Root, TimeSpan.FromSeconds(20), CancellationToken.None);
        }
    }

    public static IReadOnlyList<string> ExtractPatchPaths(string patch)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(patch, @"^\+\+\+\s+b/(.+)$", RegexOptions.Multiline))
        {
            var path = match.Groups[1].Value.Trim();
            if (path != "/dev/null")
            {
                paths.Add(path);
            }
        }

        foreach (Match match in Regex.Matches(patch, @"^---\s+a/(.+)$", RegexOptions.Multiline))
        {
            var path = match.Groups[1].Value.Trim();
            if (path != "/dev/null")
            {
                paths.Add(path);
            }
        }

        return paths.ToList();
    }

    private static async Task<string> ResolveBaseRefAsync(
        RepositoryWorkspace workspace,
        string branch,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var verify = await ProcessRunner.RunAsync(
                "git",
                ["rev-parse", "--verify", $"refs/heads/{branch}"],
                workspace.Root,
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (verify.ExitCode == 0)
            {
                return $"refs/heads/{branch}";
            }
        }

        var head = await ProcessRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", "HEAD"],
            workspace.Root,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (head.ExitCode != 0)
        {
            throw new InvalidOperationException($"无法确定仓库基线：{head.Output}");
        }

        return "HEAD";
    }
}
