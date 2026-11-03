using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Domain.Authorization;

public sealed record RevalidationResult(bool Ok, string Reason);

/// <summary>
/// Revalidates a Mandate before an ended Ticket resumes. A Policy change never revokes a live
/// Mandate immediately; this check only runs when the Ticket tries to continue.
/// </summary>
public static class MandateRevalidator
{
    public static RevalidationResult Revalidate(
        Mandate mandate,
        Source source,
        Policy policy,
        SourcePolicy sourcePolicy,
        bool sourceStillListensToThread)
    {
        if (!mandate.IsActive)
        {
            return new RevalidationResult(false, "Mandate 已被撤回或撤销。");
        }

        if (!source.Enabled)
        {
            return new RevalidationResult(false, "Source 已停用。");
        }

        if (!sourceStillListensToThread)
        {
            return new RevalidationResult(false, "Source 已不再监听该 Thread。");
        }

        if (mandate.Basis == MandateBasis.ExplicitRequest)
        {
            return new RevalidationResult(true, "明确请求的 Mandate 仍然有效。");
        }

        if (mandate.Basis == MandateBasis.FollowUp && mandate.Strength == TriggerStrength.Explicit)
        {
            return new RevalidationResult(true, "允许列表内人员的跟进 Mandate 仍然有效。");
        }

        if (policy.AcceptMode != AcceptMode.Auto && mandate.Strength != TriggerStrength.Explicit)
        {
            return new RevalidationResult(false, "推断 Mandate 需要 accept mode 为 auto。");
        }

        if (mandate.Strength == TriggerStrength.Inferred)
        {
            var missingRule = mandate.MatchedRuleIds
                .FirstOrDefault(ruleId => policy.Rules.All(rule => rule.Id != ruleId || !rule.Enabled));
            if (missingRule != Guid.Empty)
            {
                return new RevalidationResult(false, "命中的规则已被删除或停用。");
            }
        }

        if (mandate.Basis == MandateBasis.BotRule && !sourcePolicy.IsListedBot(mandate.Requester.Id))
        {
            return new RevalidationResult(false, "机器人已不在该 Source 的机器人列表中。");
        }

        var gate = policy.Gate(
            sourcePolicy,
            mandate.Requester,
            mandate.Addressed,
            threadHasTicket: mandate.Basis == MandateBasis.FollowUp);
        if (gate.Strength < mandate.Strength)
        {
            return new RevalidationResult(false, $"当前准入表只允许 {gate.Strength}，低于原 Mandate 的 {mandate.Strength}。");
        }

        return new RevalidationResult(true, "Mandate 仍满足当前 Policy。");
    }
}
