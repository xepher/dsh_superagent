using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Sources;

public sealed class EditModel(AppDbContext db, SourceProviderRegistry providers) : PageModel
{
    [BindProperty]
    public SourceForm Form { get; set; } = new();

    public IReadOnlyList<ISourceProvider> Providers => providers.All.OrderBy(provider => provider.DisplayName).ToList();
    public ISourceProvider? SelectedProvider { get; private set; }
    public Source? Existing { get; private set; }
    public bool IsNew => Existing is null;
    public IReadOnlyList<RepositoryProfile> Repositories { get; private set; } = [];
    public Dictionary<string, string> ConfigValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ConfiguredSecrets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CallbackUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? provider, CancellationToken cancellationToken)
    {
        Repositories = await LoadRepositoriesAsync(cancellationToken);
        var providerId = provider;
        if (id is { } sourceId)
        {
            Existing = await db.Sources.AsNoTracking().FirstOrDefaultAsync(source => source.Id == sourceId, cancellationToken);
            if (Existing is null)
            {
                TempData["Error"] = "Source 不存在。";
                return RedirectToPage("Index");
            }

            Form = new SourceForm
            {
                Id = Existing.Id,
                ProviderId = Existing.ProviderId,
                Name = Existing.Name,
                Enabled = Existing.Enabled,
                DefaultRepositoryId = Existing.DefaultRepositoryId,
            };
            providerId = Existing.ProviderId;
            foreach (var pair in Existing.Configuration)
            {
                ConfigValues[pair.Key] = pair.Value;
            }

            CallbackUrl = $"{Request.Scheme}://{Request.Host}/api/sources/{Existing.Id}/callback";
        }
        else
        {
            Form.ProviderId = string.IsNullOrWhiteSpace(providerId) ? providers.All.FirstOrDefault()?.Id ?? string.Empty : providerId;
            Form.Enabled = true;
        }

        SelectedProvider = providers.Find(Form.ProviderId);
        if (SelectedProvider is not null)
        {
            if (IsNew)
            {
                foreach (var field in SelectedProvider.ConfigFields)
                {
                    if (field.DefaultValue is not null)
                    {
                        ConfigValues[field.Key] = field.DefaultValue;
                    }
                }

                Form.Name = SelectedProvider.DisplayName;
            }

            foreach (var field in SelectedProvider.ConfigFields.Where(field => field.Type == ProviderFieldType.Secret))
            {
                if (ConfigValues.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    ConfiguredSecrets.Add(field.Key);
                    ConfigValues.Remove(field.Key);
                }
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Repositories = await LoadRepositoriesAsync(cancellationToken);
        SelectedProvider = providers.Find(Form.ProviderId);
        if (SelectedProvider is null)
        {
            ModelState.AddModelError(string.Empty, "请选择已注册的 Provider。");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Form.Name))
        {
            ModelState.AddModelError(string.Empty, "Source 名称不能为空。");
        }

        var duplicate = await db.Sources.AnyAsync(
            source => source.Name == Form.Name.Trim() && source.Id != Form.Id,
            cancellationToken);
        if (duplicate)
        {
            ModelState.AddModelError(string.Empty, "Source 名称已存在。");
        }

        if (Form.Id is { } id)
        {
            Existing = await db.Sources.FirstOrDefaultAsync(source => source.Id == id, cancellationToken);
            if (Existing is null)
            {
                return RedirectToPage("Index");
            }

            if (!string.Equals(Existing.ProviderId, Form.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(string.Empty, "已存在的 Source 不能更换 Provider；请新建一个 Source。");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadConfigValuesForRedisplayAsync(cancellationToken);
            return Page();
        }

        var configuration = Existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(Existing.Configuration, StringComparer.OrdinalIgnoreCase);
        foreach (var field in SelectedProvider.ConfigFields)
        {
            switch (field.Type)
            {
                case ProviderFieldType.Secret:
                    if (Request.Form[$"clear_{field.Key}"].Count > 0)
                    {
                        configuration.Remove(field.Key);
                    }
                    else
                    {
                        var secret = Request.Form[$"Config_{field.Key}"].ToString();
                        if (!string.IsNullOrWhiteSpace(secret))
                        {
                            configuration[field.Key] = secret;
                        }
                    }

                    break;
                case ProviderFieldType.Bool:
                    configuration[field.Key] = Request.Form[$"Config_{field.Key}"].Count > 0 ? "true" : "false";
                    break;
                default:
                    var value = Request.Form[$"Config_{field.Key}"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        if (field.Required)
                        {
                            ModelState.AddModelError(string.Empty, $"「{field.Label}」不能为空。");
                        }

                        configuration.Remove(field.Key);
                    }
                    else
                    {
                        configuration[field.Key] = value;
                    }

                    break;
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadConfigValuesForRedisplayAsync(cancellationToken);
            return Page();
        }

        if (Existing is null)
        {
            var source = Source.Create(Form.ProviderId, Form.Name.Trim(), configuration, Form.DefaultRepositoryId, Form.Enabled);
            await db.Sources.AddAsync(source, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = "Source 已创建。下一步配置允许用户和准入表。";
            return RedirectToPage("Edit", new { id = source.Id });
        }

        Existing.Configure(Form.Name.Trim(), configuration, Form.DefaultRepositoryId);
        Existing.SetEnabled(Form.Enabled);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "Source 配置已保存。Provider 配置变化不改写 Policy 版本。";
        return RedirectToPage("Edit", new { id = Existing.Id });
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        SelectedProvider = providers.Find(Form.ProviderId);
        if (SelectedProvider is null)
        {
            TempData["Error"] = "Provider 未注册。";
            return RedirectToPage("Index");
        }

        if (Form.Id is not { } id)
        {
            TempData["Info"] = "请先保存 Source，再测试连接。";
            return RedirectToPage("Edit", new { provider = Form.ProviderId });
        }

        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null)
        {
            TempData["Error"] = "Source 不存在。";
            return RedirectToPage("Index");
        }

        var result = await SelectedProvider.CheckAsync(source, cancellationToken);
        TempData[result.Ok ? "Success" : "Error"] = result.Detail is null ? result.Message : $"{result.Message} {result.Detail}";
        return RedirectToPage("Edit", new { id });
    }

    private async Task<IReadOnlyList<RepositoryProfile>> LoadRepositoriesAsync(CancellationToken cancellationToken) =>
        await db.Repositories.AsNoTracking()
            .OrderBy(repository => repository.DisplayName)
            .Select(repository => new RepositoryProfile
            {
                Id = repository.Id,
                DisplayName = repository.DisplayName,
                Slug = repository.Slug,
                Enabled = repository.Enabled,
            })
            .ToListAsync(cancellationToken);

    private async Task LoadConfigValuesForRedisplayAsync(CancellationToken cancellationToken)
    {
        if (SelectedProvider is null)
        {
            return;
        }

        foreach (var field in SelectedProvider.ConfigFields)
        {
            var submitted = Request.Form[$"Config_{field.Key}"].ToString();
            if (!string.IsNullOrWhiteSpace(submitted))
            {
                ConfigValues[field.Key] = submitted;
            }
        }

        if (Form.Id is { } id)
        {
            Existing = await db.Sources.AsNoTracking().FirstOrDefaultAsync(source => source.Id == id, cancellationToken);
            CallbackUrl = Existing is null ? null : $"{Request.Scheme}://{Request.Host}/api/sources/{Existing.Id}/callback";
            foreach (var field in SelectedProvider.ConfigFields.Where(field => field.Type == ProviderFieldType.Secret))
            {
                if (Existing?.GetConfig(field.Key) is { Length: > 0 })
                {
                    ConfiguredSecrets.Add(field.Key);
                }
            }
        }
    }

    public string ConfigValue(string key) => ConfigValues.TryGetValue(key, out var value) ? value : string.Empty;
    public bool BoolValue(string key) => ConfigValues.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    public bool IsSecretConfigured(string key) => ConfiguredSecrets.Contains(key);

    public sealed class SourceForm
    {
        public Guid? Id { get; set; }
        public string ProviderId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public Guid? DefaultRepositoryId { get; set; }
    }
}
