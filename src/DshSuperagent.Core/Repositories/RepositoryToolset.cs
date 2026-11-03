using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Providers.Llm;

namespace DshSuperagent.Core.Repositories;

public sealed class RepositoryToolset
{
    private readonly RepositoryWorkspace _workspace;
    private readonly int _maxOutputChars;

    public RepositoryToolset(RepositoryWorkspace workspace, int maxOutputChars)
    {
        _workspace = workspace;
        _maxOutputChars = Math.Clamp(maxOutputChars, 2_000, 40_000);
    }

    public static IReadOnlyList<LlmToolDefinition> Definitions { get; } =
    [
        new(
            "search_code",
            "在代码仓库中搜索文本或正则表达式。回答代码问题时先用它定位证据。",
            """
            {"type":"object","properties":{"query":{"type":"string","description":"要搜索的关键词或正则"},"regex":{"type":"boolean","description":"是否按正则解释 query"},"max_results":{"type":"integer","description":"最多返回多少条，1-50"}},"required":["query"]}
            """),
        new(
            "read_file",
            "读取仓库内一个文本文件的部分行，行号从 1 开始。",
            """
            {"type":"object","properties":{"path":{"type":"string","description":"仓库内相对路径"},"start_line":{"type":"integer"},"max_lines":{"type":"integer","description":"最多 200 行"}},"required":["path"]}
            """),
        new(
            "list_files",
            "列出仓库中的文本文件，可按通配符过滤，例如 src/**/*.cs。",
            """
            {"type":"object","properties":{"pattern":{"type":"string","description":"可选的通配符"},"max_results":{"type":"integer"}}}
            """),
        new(
            "git_log",
            "查看最近提交，帮助理解代码演进。",
            """
            {"type":"object","properties":{"path":{"type":"string","description":"可选的文件或目录"},"max":{"type":"integer","description":"1-30"}}}
            """),
    ];

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            return "{\"error\":\"工具参数不是合法 JSON\"}";
        }

        try
        {
            var result = name switch
            {
                "search_code" => await SearchAsync(arguments, cancellationToken),
                "read_file" => await ReadAsync(arguments, cancellationToken),
                "list_files" => await ListAsync(arguments, cancellationToken),
                "git_log" => await GitLogAsync(arguments, cancellationToken),
                _ => JsonSerializer.Serialize(new { error = $"未知工具：{name}" }),
            };

            return result.Length <= _maxOutputChars
                ? result
                : result[.._maxOutputChars] + "\n…（工具输出已截断）";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException or IOException)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> SearchAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = arguments["query"]?.GetValue<string>() ?? string.Empty;
        var regex = arguments["regex"]?.GetValue<bool>() ?? false;
        var max = arguments["max_results"]?.GetValue<int>() ?? 20;
        var matches = await _workspace.SearchAsync(query, regex, max, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            query,
            count = matches.Count,
            matches = matches.Select(match => new
            {
                path = match.Path,
                line = match.Line,
                text = match.Text.Trim(),
                context = match.Context,
            }),
        });
    }

    private async Task<string> ReadAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var path = arguments["path"]?.GetValue<string>() ?? throw new ArgumentException("缺少 path。");
        var start = arguments["start_line"]?.GetValue<int>() ?? 1;
        var max = arguments["max_lines"]?.GetValue<int>() ?? 120;
        var slice = await _workspace.ReadFileAsync(path, start, max, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            path = slice.Path,
            start_line = slice.StartLine,
            end_line = slice.EndLine,
            total_lines = slice.TotalLines,
            truncated = slice.Truncated,
            content = slice.Content,
        });
    }

    private async Task<string> ListAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var pattern = arguments["pattern"]?.GetValue<string>();
        var max = arguments["max_results"]?.GetValue<int>() ?? 200;
        var files = await _workspace.ListFilesAsync(pattern, max, cancellationToken);
        return JsonSerializer.Serialize(new { count = files.Count, files = files.Select(file => file.Path) });
    }

    private async Task<string> GitLogAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var path = arguments["path"]?.GetValue<string>();
        var max = arguments["max"]?.GetValue<int>() ?? 10;
        if (path is not null)
        {
            _ = _workspace.ResolvePath(path);
        }

        var log = await _workspace.GitLogAsync(path, max, cancellationToken);
        return JsonSerializer.Serialize(new { log });
    }
}
