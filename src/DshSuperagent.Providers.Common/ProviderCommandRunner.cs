using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshSuperagent.Providers.Common;

public sealed record ProviderCommand(string Executable, IReadOnlyList<string> Arguments, string? Input = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface IProviderCommandRunner
{
    Task<string> RunAsync(ProviderCommand command, CancellationToken cancellationToken);
    Task WatchAsync(ProviderCommand command, Func<string, CancellationToken, Task> receive,
        Func<string, CancellationToken, Task> diagnostic, CancellationToken cancellationToken);
}

public sealed class ProviderCommandException(int exitCode, int? apiCode, bool locked = false)
    : InvalidOperationException($"来源 CLI 调用失败（退出码 {exitCode}，API 状态 {apiCode?.ToString() ?? "未知"}）。")
{
    public int? ApiCode { get; } = apiCode;
    public bool Locked { get; } = locked;
}

/// <summary>No shell, no argv logging; cancellation and limits cover subprocesses and both output pipes.</summary>
public sealed class ProviderCommandRunner : IProviderCommandRunner
{
    public async Task<string> RunAsync(ProviderCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = Start(command);
        using var stop = timeout.Token.Register(() => Kill(process));
        try
        {
            async Task<string> ReadPipeAsync(StreamReader reader, int limit)
            {
                try { return await ReadAsync(reader, limit, timeout.Token); }
                catch { Kill(process); throw; }
            }
            var output = ReadPipeAsync(process.StandardOutput, 8 * 1024 * 1024);
            var error = ReadPipeAsync(process.StandardError, 1024 * 1024);
            await WriteInputAsync(process, command.Input, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).WaitAsync(timeout.Token);
            if (process.ExitCode != 0) throw Failure(process.ExitCode, await error);
            return await output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("来源 CLI 超过 90 秒未结束，将退避重试。");
        }
        finally
        {
            Kill(process);
        }
    }

    public async Task WatchAsync(ProviderCommand command, Func<string, CancellationToken, Task> receive,
        Func<string, CancellationToken, Task> diagnostic, CancellationToken cancellationToken)
    {
        using var process = Start(command);
        using var stopped = cancellationToken.Register(() => Kill(process));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? pipeFailure = null;
        async Task ReadLinesAsync(StreamReader reader, bool errors)
        {
            try
            {
                while (await ReadLineAsync(reader, abort.Token) is { } line)
                {
                    if (errors)
                    {
                        if (line.StartsWith("[event] ready", StringComparison.Ordinal)) ready.TrySetResult();
                        await diagnostic(line, abort.Token);
                    }
                    else if (!string.IsNullOrWhiteSpace(line)) await receive(line, abort.Token);
                }
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested) Interlocked.CompareExchange(ref pipeFailure, ex, null);
                abort.Cancel();
                Kill(process);
                throw;
            }
        }
        var stdout = ReadLinesAsync(process.StandardOutput, false);
        var stderr = ReadLinesAsync(process.StandardError, true);
        try
        {
            // The event CLI treats stdin EOF as a stop signal; keep this pipe open until cancellation.
            if (command.Input is not null) await process.StandardInput.WriteAsync(command.Input.AsMemory(), cancellationToken);
            var exit = process.WaitForExitAsync(abort.Token);
            var winner = await Task.WhenAny(ready.Task, exit, stdout, stderr, Task.Delay(TimeSpan.FromSeconds(45), abort.Token));
            if (winner != ready.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pipeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pipeFailure).Throw();
                throw new InvalidOperationException(exit.IsCompleted || stdout.IsCompleted || stderr.IsCompleted
                    ? "飞书事件消费者在就绪前已退出或关闭输出管道。" : "飞书事件消费者未在 45 秒内就绪。");
            }
            await Task.WhenAll(stdout, stderr, exit).WaitAsync(cancellationToken);
            throw new InvalidOperationException("飞书事件消费者已退出，将重新连接。");
        }
        catch (Exception) when (pipeFailure is not null && !cancellationToken.IsCancellationRequested)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pipeFailure).Throw();
            throw;
        }
        finally
        {
            abort.Cancel();
            Kill(process);
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception) when (abort.IsCancellationRequested) { }
        }
    }

    private static Process Start(ProviderCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Executable)) throw new InvalidOperationException("尚未配置来源 CLI。");
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var pair in command.Environment ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null) start.Environment.Remove(pair.Key);
            else start.Environment[pair.Key] = pair.Value;
        }
        start.Environment["LARKSUITE_CLI_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["LARKSUITE_CLI_NO_SKILLS_NOTIFIER"] = "1";
        try { return Process.Start(start) ?? throw new InvalidOperationException("无法启动来源 CLI。"); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("无法启动来源 CLI；请检查可执行文件路径及运行权限。");
        }
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken token)
    {
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
        process.StandardInput.Close();
    }

    private static async Task<string> ReadAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidOperationException("来源 CLI 输出超过安全大小限制。");
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= 1024 * 1024) throw new InvalidOperationException("来源事件超过 1 MiB。");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : result.ToString();
    }

    private static ProviderCommandException Failure(int exit, string stderr)
    {
        int? code = null;
        var start = stderr.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var json = JsonDocument.Parse(stderr[start..]);
                if (json.RootElement.TryGetProperty("error", out var error)
                    && error.TryGetProperty("code", out var value) && value.TryGetInt32(out var parsed)) code = parsed;
            }
            catch (JsonException) { }
        }
        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"\bHTTP (\d{3})\b");
        if (code is null && match.Success) code = int.Parse(match.Groups[1].Value);
        return new ProviderCommandException(exit, code, stderr.Contains("locked", StringComparison.OrdinalIgnoreCase));
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
