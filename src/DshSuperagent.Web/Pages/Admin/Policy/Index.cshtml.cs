using DshSuperagent.Core.Application;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Policy;

public sealed class IndexModel(AppDbContext db, PolicyChangeService policyChanges) : PageModel
{
    public int Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<SourcePolicyRow> SourcePolicies { get; private set; } = [];

    [BindProperty]
    public AcceptMode AcceptMode { get; set; }

    [BindProperty]
    public List<RuleForm> Rules { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var policy = await db.Policies.AsNoTracking()
            .Include(item => item.Rules)
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            return RedirectToPage("/Index");
        }

        Version = policy.Version;
        UpdatedAt = policy.UpdatedAt;
        AcceptMode = policy.AcceptMode;
        Rules = policy.Rules.Select(rule => new RuleForm
        {
            Id = rule.Id,
            Name = rule.Name,
            Instruction = rule.Instruction,
            Enabled = rule.Enabled,
        }).ToList();
        Rules.Add(new RuleForm { Enabled = true });
        Rules.Add(new RuleForm { Enabled = true });
        var sourceNames = await db.Sources.AsNoTracking()
            .ToDictionaryAsync(source => source.Id, source => source.Name, cancellationToken);
        SourcePolicies = policy.SourcePolicies.Select(part => new SourcePolicyRow(
            part.SourceId,
            sourceNames.TryGetValue(part.SourceId, out var name) ? name : part.SourceId.ToString("N")[..8],
            part.AllowedUsers.Count,
            part.BotIds.Count,
            part.ReceiveUnaddressed,
            part.Admission.ListedNotAddressed,
            part.Admission.UnlistedAddressed,
            part.Admission.UnlistedNotAddressed)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var policy = await db.Policies
            .Include(item => item.Rules)
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstAsync(cancellationToken);
        var existing = policy.Rules.ToDictionary(rule => rule.Id);
        var replacement = new List<PolicyRule>();
        foreach (var form in Rules)
        {
            if (form.Remove)
            {
                continue;
            }

            if (form.Id is { } ruleId && existing.TryGetValue(ruleId, out var rule))
            {
                rule.Update(form.Name ?? string.Empty, form.Instruction ?? string.Empty, form.Enabled);
                replacement.Add(rule);
            }
            else if (!string.IsNullOrWhiteSpace(form.Name))
            {
                replacement.Add(PolicyRule.Create(form.Name!, form.Instruction ?? string.Empty, form.Enabled));
            }
        }

        policy.UpdateSettings(AcceptMode, replacement);
        await db.SaveChangesAsync(cancellationToken);
        await policyChanges.NotifyOpenTicketsAsync(policy.Version, null, cancellationToken);
        TempData["Success"] = $"Policy 已更新到 v{policy.Version}。运行中的旧版本 Eval 会在下一次处理时重新排队。";
        return RedirectToPage();
    }

    public sealed class RuleForm
    {
        public Guid? Id { get; set; }
        public string? Name { get; set; } = string.Empty;
        public string? Instruction { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool Remove { get; set; }
    }

    public sealed record SourcePolicyRow(
        Guid SourceId,
        string SourceName,
        int AllowedUsers,
        int BotIds,
        bool ReceiveUnaddressed,
        AdmissionChoice ListedNotAddressed,
        AdmissionChoice UnlistedAddressed,
        AdmissionChoice UnlistedNotAddressed);
}
