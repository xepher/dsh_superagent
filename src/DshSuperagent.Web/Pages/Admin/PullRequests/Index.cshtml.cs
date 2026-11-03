using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Storage;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.PullRequests;

public sealed class IndexModel(AppDbContext db, PullRequestProviderRegistry providers, ISecretProtector protector) : PageModel
{
    public IReadOnlyList<ProfileRow> Profiles { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var entities = await db.PullRequestProfiles.AsNoTracking()
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.Name)
            .ToListAsync(cancellationToken);
        Profiles = entities.Select(entity =>
        {
            var profile = entity.ToDomain(protector);
            return new ProfileRow(
                profile.Id,
                profile.Name,
                profile.ProviderId,
                providers.Find(profile.ProviderId)?.DisplayName ?? "未注册 Provider",
                bool.TryParse(profile.GetConfig("dryRun"), out var dryRun) ? dryRun : true,
                profile.GetConfig("labels") ?? string.Empty,
                profile.IsDefault,
                profile.Enabled);
        }).ToList();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.PullRequestProfiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
        if (entity is null)
        {
            TempData["Error"] = "PR 配置不存在。";
            return RedirectToPage();
        }

        var profile = entity.ToDomain(protector);
        var provider = providers.Find(profile.ProviderId);
        if (provider is null)
        {
            TempData["Error"] = "Provider 未注册。";
            return RedirectToPage();
        }

        var result = await provider.CheckAsync(profile, cancellationToken);
        TempData[result.Ok ? "Success" : "Error"] = result.Detail is null ? result.Message : $"{result.Message} {result.Detail}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.PullRequestProfiles.FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
        if (entity is not null)
        {
            db.PullRequestProfiles.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = "PR 配置已删除。";
        }

        return RedirectToPage();
    }

    public sealed record ProfileRow(
        Guid Id,
        string Name,
        string ProviderId,
        string ProviderName,
        bool DryRun,
        string Labels,
        bool IsDefault,
        bool Enabled);
}
