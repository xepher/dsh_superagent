using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DshSuperagent.Core.Repositories;

public sealed record CodeMatch(string Path, int Line, string Text, IReadOnlyList<string> Context);

public sealed record FileSlice(
    string Path,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Content,
    bool Truncated);

public sealed record FileEntry(string Path, long Size);

public sealed class RepositoryWorkspace
{
    // Filesystem adapter: the domain never reads files directly.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "coverage", ".next", ".nuxt", "__pycache__", ".venv", "venv",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".razor", ".csproj", ".sln", ".props", ".targets", ".json", ".jsonc", ".xml", ".yml", ".yaml",
        ".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".css", ".scss", ".less", ".html", ".htm", ".md", ".txt",
        ".sql", ".py", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".ini",
        ".toml", ".config", ".env.example", ".dockerfile", ".graphql", ".proto",
    };

    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;

    public RepositoryWorkspace(
        string rootPath,
        IEnumerable<string>? includeGlobs = null,
        IEnumerable<string>? excludeGlobs = null,
        int maxFileBytes = 512 * 1024)
    {
        Root = Path.GetFullPath(rootPath);
        MaxFileBytes = maxFileBytes;
        _include = (includeGlobs ?? []).Where(static glob => !string.IsNullOrWhiteSpace(glob)).Select(GlobToRegex).ToList();
        _exclude = (excludeGlobs ?? []).Where(static glob => !string.IsNullOrWhiteSpace(glob)).Select(GlobToRegex).ToList();
    }

    public string Root { get; }
    public int MaxFileBytes { get; }

    public bool Exists => Directory.Exists(Root);

    public bool IsGitRepository => Directory.Exists(Path.Combine(Root, ".git"));

    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        string? pattern,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var matcher = string.IsNullOrWhiteSpace(pattern) ? null : GlobToRegex(pattern);
        var result = new List<FileEntry>();
        foreach (var file in EnumerateFiles(cancellationToken))
        {
            var relative = Relative(file);
            if (matcher is not null && !matcher.IsMatch(relative))
            {
                continue;
            }

            result.Add(new FileEntry(relative, new FileInfo(file).Length));
            if (result.Count >= Math.Clamp(maxResults, 1, 500))
            {
                break;
            }
        }

        await Task.CompletedTask;
        return result;
    }

    public async Task<IReadOnlyList<CodeMatch>> SearchAsync(
        string query,
        bool useRegex,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        Regex matcher;
        try
        {
            matcher = useRegex
                ? new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
                : new Regex(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"无效的搜索表达式：{ex.Message}", nameof(query), ex);
        }

        var matches = new List<CodeMatch>();
        var limit = Math.Clamp(maxResults, 1, 100);
        foreach (var file in EnumerateFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (lines.Length == 0 || lines.Length > 20_000)
            {
                continue;
            }

            for (var index = 0; index < lines.Length; index++)
            {
                Match match;
                try
                {
                    match = matcher.Match(lines[index]);
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }

                if (!match.Success)
                {
                    continue;
                }

                var context = new List<string>();
                for (var contextIndex = Math.Max(0, index - 2); contextIndex <= Math.Min(lines.Length - 1, index + 2); contextIndex++)
                {
                    context.Add($"{contextIndex + 1}: {lines[contextIndex]}");
                }

                matches.Add(new CodeMatch(Relative(file), index + 1, lines[index], context));
                if (matches.Count >= limit)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    public async Task<FileSlice> ReadFileAsync(
        string path,
        int startLine,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"文件不存在：{path}");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidOperationException($"文件超过 {MaxFileBytes / 1024} KB 限制：{path}");
        }

        if (!IsTextFile(fullPath))
        {
            throw new InvalidOperationException($"不支持读取二进制文件：{path}");
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var start = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        var count = Math.Clamp(maxLines, 1, 250);
        var end = Math.Min(lines.Length, start + count - 1);
        var builder = new StringBuilder();
        for (var index = start - 1; index < end; index++)
        {
            builder.Append(index + 1).Append(": ").AppendLine(lines[index]);
        }

        return new FileSlice(Relative(fullPath), start, end, lines.Length, builder.ToString(), end < lines.Length);
    }

    public async Task<string> GitLogAsync(string? path, int max, CancellationToken cancellationToken)
    {
        if (!IsGitRepository)
        {
            return "当前目录不是 Git 仓库。";
        }

        var arguments = new List<string> { "log", $"-n{Math.Clamp(max, 1, 30)}", "--oneline", "--decorate" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            arguments.Add("--");
            arguments.Add(path);
        }

        var result = await ProcessRunner.RunAsync("git", arguments, Root, TimeSpan.FromSeconds(20), cancellationToken);
        return result.ExitCode == 0 ? result.Output : $"git log 失败：{result.Output}";
    }

    public string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("路径不能为空。", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("只允许仓库内相对路径。");
        }

        var combined = Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(combined))
        {
            throw new InvalidOperationException("路径超出仓库范围。");
        }

        if (File.Exists(combined))
        {
            var target = new FileInfo(combined).ResolveLinkTarget(true);
            if (target is not null && !IsUnderRoot(Path.GetFullPath(target.FullName)))
            {
                throw new InvalidOperationException("符号链接指向仓库外部。");
            }
        }

        return combined;
    }

    public bool IsUnderRoot(string fullPath)
    {
        var normalizedRoot = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateFiles(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException)
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    var name = Path.GetFileName(entry);
                    if (!SkippedDirectories.Contains(name))
                    {
                        stack.Push(entry);
                    }

                    continue;
                }

                var relative = Relative(entry);
                if (!IsCandidate(relative, entry))
                {
                    continue;
                }

                yield return entry;
            }
        }
    }

    private bool IsCandidate(string relative, string fullPath)
    {
        if (!IsTextFile(fullPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(relative);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && !fileName.Equals(".env.example", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_exclude.Any(regex => regex.IsMatch(relative)))
        {
            return false;
        }

        return _include.Count == 0 || _include.Any(regex => regex.IsMatch(relative));
    }

    private static bool IsTextFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            var name = Path.GetFileName(path);
            return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase);
        }

        return TextExtensions.Contains(extension);
    }

    private string Relative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Trim().Replace('\\', '/');
        var hasSlash = normalized.Contains('/');
        var builder = new StringBuilder("^");
        if (!hasSlash)
        {
            builder.Append("(?:.*/)?");
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character == '*')
            {
                if (index + 1 < normalized.Length && normalized[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

public sealed record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ProcessResult(-1, $"命令执行超过 {timeout.TotalSeconds:F0} 秒，已终止。");
        }

        var output = (await stdout + "\n" + await stderr).Trim();
        if (output.Length > 20_000)
        {
            output = output[..20_000] + "\n…（输出已截断）";
        }

        return new ProcessResult(process.ExitCode, output);
    }
}
