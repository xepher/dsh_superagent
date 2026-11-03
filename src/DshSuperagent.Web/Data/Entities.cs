namespace DshSuperagent.Web.Data;

public sealed class RepositoryEntity
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string? RemoteUrl { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? Instructions { get; set; }
    public string IncludeGlobsJson { get; set; } = "[]";
    public string ExcludeGlobsJson { get; set; } = "[]";
    public bool Enabled { get; set; }
    public bool AutoReviewEnabled { get; set; }
    public bool AllowAutonomousFix { get; set; }
    public string? ValidationCommand { get; set; }
    public int MaxPatchFiles { get; set; } = 5;
    public string? PullRequestOwner { get; set; }
    public string? PullRequestRepository { get; set; }
    public Guid? PullRequestProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LlmProfileEntity
{
    public Guid Id { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PullRequestProfileEntity
{
    public Guid Id { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SettingsEntity
{
    public int Id { get; set; }
    public string AgentSettingsJson { get; set; } = "{}";
    public string TeamPromptsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Actor { get; set; }
}

public sealed class StreamReplyEntity
{
    public string StreamId { get; set; } = string.Empty;
    public bool Finished { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
