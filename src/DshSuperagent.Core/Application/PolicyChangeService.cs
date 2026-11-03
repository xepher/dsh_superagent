using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Core.Application;

/// <summary>
/// A Policy change does not revoke live Mandates. Affected open Tickets receive a stored
/// PolicyChange Input, and the Mandate is revalidated only when the Ticket tries to resume.
/// </summary>
public sealed class PolicyChangeService(
    ITicketRepository tickets,
    IUnitOfWork unitOfWork)
{
    public async Task NotifyOpenTicketsAsync(
        int newPolicyVersion,
        Guid? sourceId,
        CancellationToken cancellationToken)
    {
        var open = await tickets.GetOpenTicketsAsync(cancellationToken);
        foreach (var ticket in open.Where(ticket =>
                     ticket.Mandate.PolicyVersion < newPolicyVersion
                     && (sourceId is null || ticket.SourceId == sourceId)))
        {
            ticket.AddContextInput(
                InputKind.PolicyChange,
                $"Policy 已更新到 v{newPolicyVersion}。这个工单的 Mandate 属于 v{ticket.Mandate.PolicyVersion}；"
                + "运行中的工作不被立即撤销，恢复或继续前会重新校验。");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
