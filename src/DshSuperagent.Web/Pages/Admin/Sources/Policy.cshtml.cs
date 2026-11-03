using DshSuperagent.Core.Application;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Sources;

public sealed class PolicyModel(AppDbContext db, PolicyChangeService policyChanges) : PageModel
{
    public Source Source { get; private set; } = null!;
    public int PolicyVersion { get; private set; }
    public AcceptMode AcceptMode { get; private set; }

    [BindProperty]
    public string? AllowedUsersText { get; set; } = string.Empty;

    [BindProperty]
    public string? BotIdsText { get; set; } = string.Empty;

    [BindProperty]
    public bool ReceiveUnaddressed { get; set; }

    [BindProperty]
    public AdmissionChoice ListedNotAddressed { get; set; } = AdmissionChoice.Inferred;

    [BindProperty]
    public AdmissionChoice UnlistedAddressed { get; set; } = AdmissionChoice.Ignore;

    [BindProperty]
    public AdmissionChoice UnlistedNotAddressed { get; set; } = AdmissionChoice.Ignore;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null)
        {
            return RedirectToPage("Index");
        }

        Source = source;
        var policy = await db.Policies.AsNoTracking()
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            return RedirectToPage("Index");
        }

        PolicyVersion = policy.Version;
        AcceptMode = policy.AcceptMode;
        var part = policy.SourcePolicies.FirstOrDefault(item => item.SourceId == id) ?? new SourcePolicy(id);
        AllowedUsersText = string.Join('\n', part.AllowedUsers.Select(user =>
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserId : $"{user.UserId}, {user.DisplayName}"));
        BotIdsText = string.Join('\n', part.BotIds);
        ReceiveUnaddressed = part.ReceiveUnaddressed;
        ListedNotAddressed = part.Admission.ListedNotAddressed;
        UnlistedAddressed = part.Admission.UnlistedAddressed;
        UnlistedNotAddressed = part.Admission.UnlistedNotAddressed;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.Sources.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null)
        {
            return RedirectToPage("Index");
        }

        var policy = await db.Policies
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            return RedirectToPage("Index");
        }

        var users = ParseUsers(AllowedUsersText);
        var bots = (BotIdsText ?? string.Empty).Split(['\n', '\r', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        policy.UpdateSourcePolicy(
            id,
            users,
            bots,
            new AdmissionTable(ListedNotAddressed, UnlistedAddressed, UnlistedNotAddressed),
            ReceiveUnaddressed);
        await db.SaveChangesAsync(cancellationToken);
        await policyChanges.NotifyOpenTicketsAsync(policy.Version, id, cancellationToken);
        TempData["Success"] = $"Source Policy 已保存，Policy 版本提升到 v{policy.Version}。运行中的旧版本 Eval 会在下一次处理时重新排队。";
        return RedirectToPage("Policy", new { id });
    }

    private static List<AllowedUser> ParseUsers(string? text)
    {
        var users = new List<AllowedUser>();
        foreach (var line in (text ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([',', '，', '\t'], StringSplitOptions.TrimEntries);
            var id = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var display = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            users.Add(new AllowedUser(id, display));
        }

        return users
            .GroupBy(user => user.UserId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }
}
