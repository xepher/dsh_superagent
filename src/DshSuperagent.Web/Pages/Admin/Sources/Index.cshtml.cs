using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Sources;

public sealed class IndexModel(AppDbContext db, SourceProviderRegistry providers) : PageModel
{
    public IReadOnlyList<SourceRow> Sources { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var policy = await db.Policies.AsNoTracking()
            .Include(item => item.SourcePolicies)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var sources = await db.Sources.AsNoTracking()
            .OrderBy(source => source.Name)
            .ToListAsync(cancellationToken);
        var repositoryNames = await db.Repositories.AsNoTracking()
            .ToDictionaryAsync(repository => repository.Id, repository => repository.DisplayName, cancellationToken);
        Sources = sources.Select(source =>
        {
            var provider = providers.Find(source.ProviderId);
            var part = policy?.SourcePolicies.FirstOrDefault(item => item.SourceId == source.Id);
            var missing = provider is null
                          || (provider.ConfigFields.Any(field => field.Required)
                              && provider.ConfigFields.Where(field => field.Required).Any(field =>
                                  string.IsNullOrWhiteSpace(source.GetConfig(field.Key))));
            return new SourceRow(
                source.Id,
                source.Name,
                source.ProviderId,
                provider?.DisplayName ?? "未注册 Provider",
                source.Enabled,
                provider?.Capabilities.Callback ?? false,
                part?.AllowedUsers.Count ?? 0,
                part?.BotIds.Count ?? 0,
                source.DefaultRepositoryId,
                source.DefaultRepositoryId is { } repositoryId && repositoryNames.TryGetValue(repositoryId, out var name) ? name : null,
                source.ReadPosition,
                missing,
                CallbackUrl(source));
        }).ToList();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null)
        {
            TempData["Error"] = "Source 不存在。";
            return RedirectToPage();
        }

        var provider = providers.Find(source.ProviderId);
        if (provider is null)
        {
            TempData["Error"] = "Provider 未注册。";
            return RedirectToPage();
        }

        var result = await provider.CheckAsync(source, cancellationToken);
        TempData[result.Ok ? "Success" : "Error"] = result.Detail is null ? result.Message : $"{result.Message} {result.Detail}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.Sources.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null)
        {
            TempData["Error"] = "Source 不存在。";
            return RedirectToPage();
        }

        if (await db.Threads.AnyAsync(thread => thread.SourceId == id, cancellationToken))
        {
            TempData["Error"] = "Source 已有 Thread/Trigger 历史，不能删除；请停用。";
            return RedirectToPage();
        }

        db.Sources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "Source 已删除。";
        return RedirectToPage();
    }

    private string CallbackUrl(Source source) =>
        $"{Request.Scheme}://{Request.Host}/api/sources/{source.Id}/callback";

    public sealed record SourceRow(
        Guid Id,
        string Name,
        string ProviderId,
        string ProviderDisplayName,
        bool Enabled,
        bool IsCallback,
        int AllowedUsers,
        int BotIds,
        Guid? DefaultRepositoryId,
        string? DefaultRepositoryName,
        string? ReadPosition,
        bool HasConfigIssue,
        string CallbackUrl);
}
