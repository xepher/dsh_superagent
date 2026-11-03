using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Storage;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Models;

public sealed class IndexModel(AppDbContext db, LlmProviderRegistry providers, ISecretProtector protector) : PageModel
{
    public IReadOnlyList<ModelRow> Models { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var entities = await db.LlmProfiles.AsNoTracking()
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.Name)
            .ToListAsync(cancellationToken);
        Models = entities.Select(entity =>
        {
            var profile = entity.ToDomain(protector);
            return new ModelRow(
                profile.Id,
                profile.Name,
                profile.ProviderId,
                providers.Find(profile.ProviderId)?.DisplayName ?? "未注册 Provider",
                profile.GetConfig("model") ?? "—",
                profile.IsDefault,
                profile.Enabled);
        }).ToList();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.LlmProfiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
        if (entity is null)
        {
            TempData["Error"] = "模型配置不存在。";
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
        var entity = await db.LlmProfiles.FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
        if (entity is not null)
        {
            db.LlmProfiles.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = "模型配置已删除。";
        }

        return RedirectToPage();
    }

    public sealed record ModelRow(
        Guid Id,
        string Name,
        string ProviderId,
        string ProviderName,
        string Model,
        bool IsDefault,
        bool Enabled);
}
