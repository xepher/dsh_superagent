namespace DshSuperagent.Core.Configuration;

/// <summary>Deployment settings for the code executor. These are not part of the authorization Policy.</summary>
public sealed class RepositoryProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string? RemoteUrl { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? Instructions { get; set; }
    public List<string> IncludeGlobs { get; set; } = [];
    public List<string> ExcludeGlobs { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public bool AutoReviewEnabled { get; set; }
    public bool AllowAutonomousFix { get; set; }
    public string? ValidationCommand { get; set; }
    public int MaxPatchFiles { get; set; } = 5;
    public string? PullRequestOwner { get; set; }
    public string? PullRequestRepository { get; set; }
    public Guid? PullRequestProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LlmProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? GetConfig(string key) =>
        Configuration.TryGetValue(key, out var value) ? value : null;
}

public sealed class PullRequestProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? GetConfig(string key) =>
        Configuration.TryGetValue(key, out var value) ? value : null;
}

public sealed class AgentSettings
{
    public int MaxAgentIterations { get; set; } = 8;
    public int MaxToolOutputChars { get; set; } = 12_000;
    public int MaxConcurrentAssignments { get; set; } = 2;
    public int ReviewIntervalHours { get; set; } = 12;
    public int MaxReviewFiles { get; set; } = 400;
    public int PatchValidationTimeoutSeconds { get; set; } = 180;
    public int MaxAutoFixesPerRun { get; set; } = 2;
    public bool AutoReviewEnabled { get; set; } = true;
}

public sealed class TeamPrompts
{
    public string Communication { get; set; } =
        "只在同事需要行动或可以使用结果时发言。确认一次并说明将做什么，完成后给出结果；失败如实报告。";
    public string TicketScope { get; set; } =
        "局部、可验证的修改可以做到可审查的 PR。需要产品取舍或设计决策时，停下来提出一个具体问题。";
    public string DeliveryRequirements { get; set; } =
        "PR 必须说明改动、证据和验证命令。默认创建草稿 PR，等待人工复核后合并；不要自动合并。";
}
