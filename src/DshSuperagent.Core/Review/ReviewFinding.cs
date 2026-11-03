namespace DshSuperagent.Core.Review;

public enum FindingSeverity
{
    Info,
    Warning,
    High,
}

/// <summary>
/// A deterministic static-analysis finding. It is not a domain entity: the Source turns it into an
/// immutable Trigger whose payload carries the finding facts to a future Executor.
/// </summary>
public sealed class ReviewFinding
{
    public string RuleId { get; init; } = string.Empty;
    public FindingSeverity Severity { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;

    public string Signature() =>
        $"{RuleId}:{FilePath}:{Line}:{Title}".ToLowerInvariant();
}

public enum FixStatus
{
    PatchReady,
    PullRequestOpen,
    Failed,
}
