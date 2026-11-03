namespace DshSuperagent.Core.Commands;

public enum BotCommandKind
{
    Question,
    Help,
    ListRepositories,
    Review,
    Fix,
    Stop,
    Status,
    Unknown,
}

public sealed record BotCommand(
    BotCommandKind Kind,
    string? RepositorySlug = null,
    Guid? FindingId = null,
    string? Argument = null,
    string Question = "");

public static class BotCommandParser
{
    public static BotCommand Parse(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return new BotCommand(BotCommandKind.Question, Question: trimmed);
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        return verb switch
        {
            "/help" or "/?" => new BotCommand(BotCommandKind.Help, Argument: parts.Length > 1 ? string.Join(' ', parts[1..]) : null),
            "/repos" => new BotCommand(BotCommandKind.ListRepositories),
            "/status" => new BotCommand(BotCommandKind.Status),
            "/stop" or "/停止" => new BotCommand(BotCommandKind.Stop),
            "/repo" when parts.Length >= 3 => new BotCommand(
                BotCommandKind.Question,
                RepositorySlug: parts[1],
                Question: string.Join(' ', parts[2..])),
            "/review" => new BotCommand(
                BotCommandKind.Review,
                RepositorySlug: parts.Length >= 2 ? parts[1] : null),
            "/fix" => ParseFix(parts),
            _ => new BotCommand(BotCommandKind.Unknown, Argument: parts[0]),
        };
    }

    private static BotCommand ParseFix(string[] parts)
    {
        string? slug = null;
        Guid? findingId = null;
        for (var index = 1; index < parts.Length; index++)
        {
            if (Guid.TryParse(parts[index], out var parsed))
            {
                findingId = parsed;
            }
            else if (slug is null)
            {
                slug = parts[index];
            }
        }

        return new BotCommand(BotCommandKind.Fix, RepositorySlug: slug, FindingId: findingId);
    }

    public static string HelpText(string botName) =>
        $"""
        **{botName} 使用方式**

        - 直接提问：我会在代码仓库中检索并回答，例如「用户列表页的分页逻辑在哪里实现？」
        - `/repo <仓库 slug> <问题>`：指定仓库提问
        - `/repos`：列出可用仓库
        - `/review [仓库 slug]`：触发一次静态代码审查（需要操作权限）
        - `/fix [仓库 slug] [发现 ID]`：生成修复补丁并按配置创建 PR（需要操作权限）
        - `/stop`：停止当前 Thread 上需要人工停止的工单
        - `/status`：查看当前配置状态
        - `/help`：显示本帮助

        回答基于已配置仓库的真实代码；找不到证据时会明确说明。
        """;
}
