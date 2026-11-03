using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.PullRequests;

public sealed class EditModel(AppDbContext db, PullRequestProviderRegistry providers, ISecretProtector protector) : PageModel
{
    [BindProperty]
    public ProfileForm Form { get; set; } = new();

    public IReadOnlyList<IPullRequestProvider> Providers => providers.All.OrderBy(provider => provider.DisplayName).ToList();
    public IPullRequestProvider? SelectedProvider { get; private set; }
    public PullRequestProfile? Existing { get; private set; }
    public bool IsNew => Existing is null;
    public Dictionary<string, string> ConfigValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ConfiguredSecrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IActionResult> OnGetAsync(Guid? id, string? provider, CancellationToken cancellationToken)
    {
        if (id is { } profileId)
        {
            var entity = await db.PullRequestProfiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Id == profileId, cancellationToken);
            if (entity is null)
            {
                return RedirectToPage("Index");
            }

            Existing = entity.ToDomain(protector);
            Form = new ProfileForm
            {
                Id = Existing.Id,
                ProviderId = Existing.ProviderId,
                Name = Existing.Name,
                Enabled = Existing.Enabled,
                IsDefault = Existing.IsDefault,
            };
            foreach (var pair in Existing.Configuration)
            {
                ConfigValues[pair.Key] = pair.Value;
            }
        }
        else
        {
            Form.ProviderId = string.IsNullOrWhiteSpace(provider) ? providers.All.FirstOrDefault()?.Id ?? string.Empty : provider;
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
        SelectedProvider = providers.Find(Form.ProviderId);
        if (SelectedProvider is null)
        {
            ModelState.AddModelError(string.Empty, "Provider 未注册。");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Form.Name))
        {
            ModelState.AddModelError(string.Empty, "名称不能为空。");
        }

        if (await db.PullRequestProfiles.AnyAsync(profile => profile.Name == Form.Name.Trim() && profile.Id != Form.Id, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "名称已存在。");
        }

        PullRequestProfileEntity? entity = null;
        if (Form.Id is { } id)
        {
            entity = await db.PullRequestProfiles.FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
            if (entity is null)
            {
                return RedirectToPage("Index");
            }
        }

        if (!ModelState.IsValid)
        {
            foreach (var field in SelectedProvider.ConfigFields)
            {
                var submitted = Request.Form[$"Config_{field.Key}"].ToString();
                if (!string.IsNullOrWhiteSpace(submitted))
                {
                    ConfigValues[field.Key] = submitted;
                }
            }

            return Page();
        }

        var profile = entity is null ? new PullRequestProfile() : entity.ToDomain(protector);
        profile.ProviderId = Form.ProviderId;
        profile.Name = Form.Name.Trim();
        profile.Enabled = Form.Enabled;
        profile.IsDefault = Form.IsDefault;
        var configuration = new Dictionary<string, string>(profile.Configuration, StringComparer.OrdinalIgnoreCase);
        foreach (var field in SelectedProvider.ConfigFields)
        {
            if (field.Type == ProviderFieldType.Secret)
            {
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
            }
            else if (field.Type == ProviderFieldType.Bool)
            {
                configuration[field.Key] = Request.Form[$"Config_{field.Key}"].Count > 0 ? "true" : "false";
            }
            else
            {
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
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        profile.Configuration = configuration;
        if (Form.IsDefault)
        {
            foreach (var other in await db.PullRequestProfiles.Where(item => item.Id != profile.Id && item.IsDefault).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
            }
        }

        entity ??= new PullRequestProfileEntity { Id = profile.Id, CreatedAt = DateTimeOffset.UtcNow };
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }

        profile.CreatedAt = entity.CreatedAt;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Apply(profile, protector);
        if (db.Entry(entity).State == EntityState.Detached)
        {
            await db.PullRequestProfiles.AddAsync(entity, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "PR 配置已保存。";
        return RedirectToPage("Edit", new { id = profile.Id });
    }

    public string ConfigValue(string key) => ConfigValues.TryGetValue(key, out var value) ? value : string.Empty;
    public bool BoolValue(string key) => ConfigValues.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    public bool IsSecretConfigured(string key) => ConfiguredSecrets.Contains(key);

    public sealed class ProfileForm
    {
        public Guid? Id { get; set; }
        public string ProviderId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; }
    }
}
