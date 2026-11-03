using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Events;

namespace DshSuperagent.Domain.Sources;

public enum ActorKind
{
    Human,
    Bot,
    System,
}

public sealed record TriggerActor(string Id, string DisplayName, ActorKind Kind)
{
    public bool IsHuman => Kind == ActorKind.Human;
}

public sealed class PolicyRule
{
    private PolicyRule()
    {
    }

    private PolicyRule(Guid id, string name, string instruction, bool enabled)
    {
        Id = id;
        Name = name;
        Instruction = instruction;
        Enabled = enabled;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Instruction { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }

    public static PolicyRule Create(string name, string instruction, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("规则名称不能为空。");
        }

        return new PolicyRule(Guid.NewGuid(), name.Trim(), instruction.Trim(), enabled);
    }

    public void Update(string name, string instruction, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("规则名称不能为空。");
        }

        Name = name.Trim();
        Instruction = instruction.Trim();
        Enabled = enabled;
    }
}

/// <summary>
/// The Policy aggregate owns the versioned assessment rules, the accept mode and each Source's
/// admission table. A Mandate records the Policy version it was accepted under.
/// </summary>
public sealed class Policy : AggregateRoot
{
    private readonly List<PolicyRule> _rules = [];
    private readonly List<SourcePolicy> _sourcePolicies = [];

    private Policy()
    {
    }

    public int Version { get; private set; }
    public AcceptMode AcceptMode { get; private set; }
    public IReadOnlyList<PolicyRule> Rules => _rules;
    public IReadOnlyList<SourcePolicy> SourcePolicies => _sourcePolicies;
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Policy CreateDefault()
    {
        var policy = new Policy
        {
            Version = 1,
            AcceptMode = AcceptMode.Observe,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        policy._rules.Add(PolicyRule.Create(
            "code-quality",
            "当定时代码审查发现 High 或 Warning 级问题，且目标仓库允许自动修复时，创建一个修复工单；Info 级问题只记录。"));
        return policy;
    }

    public SourcePolicy GetSourcePolicy(Guid sourceId)
    {
        var existing = _sourcePolicies.FirstOrDefault(sourcePolicy => sourcePolicy.SourceId == sourceId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new SourcePolicy(sourceId);
        _sourcePolicies.Add(created);
        return created;
    }

    public void SetAcceptMode(AcceptMode mode)
    {
        if (AcceptMode == mode)
        {
            return;
        }

        AcceptMode = mode;
        Bump();
    }

    public void UpdateSettings(AcceptMode mode, IEnumerable<PolicyRule> rules)
    {
        var replacement = rules.ToList();
        var duplicate = replacement
            .GroupBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainException($"规则名称重复：{duplicate.Key}");
        }

        AcceptMode = mode;
        _rules.Clear();
        _rules.AddRange(replacement);
        Bump();
    }

    public void ReplaceRules(IEnumerable<PolicyRule> rules)
    {
        var replacement = rules.ToList();
        var duplicate = replacement
            .GroupBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainException($"规则名称重复：{duplicate.Key}");
        }

        _rules.Clear();
        _rules.AddRange(replacement);
        Bump();
    }

    public void UpdateSourcePolicy(
        Guid sourceId,
        IEnumerable<AllowedUser> allowedUsers,
        IEnumerable<string> botIds,
        AdmissionTable admission,
        bool receiveUnaddressed)
    {
        GetSourcePolicy(sourceId).Update(allowedUsers, botIds, admission, receiveUnaddressed);
        Bump();
    }

    /// <summary>
    /// The deterministic gate. Assessment (the model layer) runs only for admitted inferred candidates.
    /// </summary>
    public GateResult Gate(
        SourcePolicy sourcePolicy,
        TriggerActor actor,
        bool addressed,
        bool threadHasTicket)
    {
        if (actor.Kind == ActorKind.System)
        {
            return AcceptMode == AcceptMode.Off
                ? new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "accept mode off 拒绝 Source 生成的推断候选。")
                : new GateResult(TriggerStrength.Inferred, GateBasis.BotCandidate, "Source 自己生成的自动化候选，需要评估。");
        }

        if (!actor.IsHuman)
        {
            if (!sourcePolicy.IsListedBot(actor.Id))
            {
                return new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "机器人不在该 Source 的机器人列表中。");
            }

            if (!addressed && !sourcePolicy.ReceiveUnaddressed)
            {
                return new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "机器人消息没有 @ 智能体，且 Source 不接收全部消息。");
            }

            return AcceptMode == AcceptMode.Off
                ? new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "accept mode off 拒绝机器人推断候选。")
                : new GateResult(TriggerStrength.Inferred, GateBasis.BotCandidate, "机器人列表内的候选，需要评估。");
        }

        var listed = sourcePolicy.IsListed(actor.Id);

        // A subscribed Thread can deliver a follow-up even when the Source only receives mentions;
        // ownership is decided by the application, while the Gate still assigns its strength.
        if (!addressed && !sourcePolicy.ReceiveUnaddressed && !threadHasTicket)
        {
            return new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "消息没有 @ 智能体，Source 也不接收未 @ 的消息。");
        }

        var cell = sourcePolicy.Admission.CellFor(listed, addressed);
        if (cell == AdmissionChoice.Explicit)
        {
            return new GateResult(TriggerStrength.Explicit, GateBasis.ExplicitAdmissionCell, "准入表将该接收范围设为明确请求。");
        }

        if (listed && addressed)
        {
            return new GateResult(TriggerStrength.Explicit, GateBasis.ExplicitMention, "允许列表内的人明确 @ 了智能体。");
        }

        if (threadHasTicket && listed)
        {
            return new GateResult(TriggerStrength.Explicit, GateBasis.FollowUpListed, "允许列表内的人在已有工单的 Thread 上跟进。");
        }

        if (threadHasTicket && !listed)
        {
            if (cell == AdmissionChoice.Ignore)
            {
                return new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "准入表忽略未列入允许列表人员的跟进。");
            }

            return AcceptMode == AcceptMode.Off
                ? new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "未列入允许列表的跟进在 accept mode off 下不被接纳。")
                : new GateResult(TriggerStrength.Inferred, GateBasis.FollowUpUnlisted, "未列入允许列表的人在已有工单的 Thread 上跟进，属于推断候选。");
        }

        if (cell == AdmissionChoice.Ignore)
        {
            return new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "准入表忽略该发送者和接收范围。");
        }

        return AcceptMode == AcceptMode.Off
            ? new GateResult(TriggerStrength.NotAdmitted, GateBasis.Ignored, "accept mode off 只允许明确请求。")
            : new GateResult(TriggerStrength.Inferred, GateBasis.AdmissionCell, "准入表允许该消息成为推断候选。");
    }

    private void Bump()
    {
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
        Raise(new PolicyChanged(Version, AcceptMode.ToString(), UpdatedAt));
    }
}
