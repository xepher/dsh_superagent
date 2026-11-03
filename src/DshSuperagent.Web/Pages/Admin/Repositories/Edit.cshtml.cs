using System.Text.RegularExpressions;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Repositories;

public sealed class EditModel(AppDbContext db, DshSuperagent.Core.Storage.ISecretProtector protector) : PageModel
{
    [BindProperty]
    public RepositoryForm Form { get; set; } = new();

    public bool IsNew => Form.Id is null;
    public IReadOnlyList<PullRequestProfile> PullRequestProfiles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken cancellationToken)
    {
        PullRequestProfiles = await LoadProfilesAsync(cancellationToken);
        if (id is { } repositoryId)
        {
            var entity = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(item => item.Id == repositoryId, cancellationToken);
            if (entity is null)
            {
                return RedirectToPage("Index");
            }

            var repository = entity.ToDomain();
            Form = new RepositoryForm
            {
                Id = repository.Id,
                Slug = repository.Slug,
                DisplayName = repository.DisplayName,
                LocalPath = repository.LocalPath,
                RemoteUrl = repository.RemoteUrl,
                DefaultBranch = repository.DefaultBranch,
                Instructions = repository.Instructions,
                IncludeGlobsText = string.Join('\n', repository.IncludeGlobs),
                ExcludeGlobsText = string.Join('\n', repository.ExcludeGlobs),
                Enabled = repository.Enabled,
                AutoReviewEnabled = repository.AutoReviewEnabled,
                AllowAutonomousFix = repository.AllowAutonomousFix,
                ValidationCommand = repository.ValidationCommand,
                MaxPatchFiles = repository.MaxPatchFiles,
                PullRequestOwner = repository.PullRequestOwner,
                PullRequestRepository = repository.PullRequestRepository,
                PullRequestProfileId = repository.PullRequestProfileId,
            };
        }
        else
        {
            Form.Enabled = true;
            Form.DefaultBranch = "main";
            Form.MaxPatchFiles = 5;
            Form.ExcludeGlobsText = "bin/\nobj/\nnode_modules/\ndist/";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        PullRequestProfiles = await LoadProfilesAsync(cancellationToken);
        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        RepositoryEntity entity;
        if (Form.Id is { } id)
        {
            entity = await db.Repositories.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                     ?? throw new InvalidOperationException("仓库不存在。");
        }
        else
        {
            entity = new RepositoryEntity { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            await db.Repositories.AddAsync(entity, cancellationToken);
        }

        var profile = entity.ToDomain();
        profile.Slug = Form.Slug.Trim().ToLowerInvariant();
        profile.DisplayName = Form.DisplayName.Trim();
        profile.LocalPath = Form.LocalPath.Trim();
        profile.RemoteUrl = string.IsNullOrWhiteSpace(Form.RemoteUrl) ? null : Form.RemoteUrl.Trim();
        profile.DefaultBranch = Form.DefaultBranch.Trim();
        profile.Instructions = string.IsNullOrWhiteSpace(Form.Instructions) ? null : Form.Instructions.Trim();
        profile.IncludeGlobs = SplitLines(Form.IncludeGlobsText ?? string.Empty);
        profile.ExcludeGlobs = SplitLines(Form.ExcludeGlobsText ?? string.Empty);
        profile.Enabled = Form.Enabled;
        profile.AutoReviewEnabled = Form.AutoReviewEnabled;
        profile.AllowAutonomousFix = Form.AllowAutonomousFix;
        profile.ValidationCommand = string.IsNullOrWhiteSpace(Form.ValidationCommand) ? null : Form.ValidationCommand.Trim();
        profile.MaxPatchFiles = Form.MaxPatchFiles;
        profile.PullRequestOwner = string.IsNullOrWhiteSpace(Form.PullRequestOwner) ? null : Form.PullRequestOwner.Trim();
        profile.PullRequestRepository = string.IsNullOrWhiteSpace(Form.PullRequestRepository) ? null : Form.PullRequestRepository.Trim();
        profile.PullRequestProfileId = Form.PullRequestProfileId;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Apply(profile);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "代码仓库配置已保存。";
        return RedirectToPage("Edit", new { id = entity.Id });
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken cancellationToken)
    {
        PullRequestProfiles = await LoadProfilesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(Form.LocalPath))
        {
            TempData["Error"] = "请先填写本地路径。";
            return Page();
        }

        var workspace = new DshSuperagent.Core.Repositories.RepositoryWorkspace(
            Form.LocalPath,
            SplitLines(Form.IncludeGlobsText ?? string.Empty),
            SplitLines(Form.ExcludeGlobsText ?? string.Empty));
        if (!workspace.Exists)
        {
            TempData["Error"] = $"目录不存在：{workspace.Root}";
            return Page();
        }

        if (!workspace.IsGitRepository)
        {
            TempData["Error"] = $"目录存在，但不是 Git 仓库：{workspace.Root}";
            return Page();
        }

        var files = await workspace.ListFilesAsync("**/*.cs", 5, cancellationToken);
        TempData["Success"] = $"Git 仓库可读，枚举到 {files.Count} 个 C# 文件（示例上限 5 个）。";
        return Page();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Form.Slug))
        {
            ModelState.AddModelError(string.Empty, "slug 不能为空。");
        }
        else if (!Regex.IsMatch(Form.Slug, "^[a-z0-9][a-z0-9-]{0,40}$"))
        {
            ModelState.AddModelError(string.Empty, "slug 只能包含小写字母、数字和连字符，最长 41 个字符。");
        }

        var slug = Form.Slug.Trim().ToLowerInvariant();
        if (db.Repositories.Any(repository => repository.Slug == slug && repository.Id != Form.Id))
        {
            ModelState.AddModelError(string.Empty, "slug 已存在。");
        }

        if (string.IsNullOrWhiteSpace(Form.DisplayName))
        {
            ModelState.AddModelError(string.Empty, "显示名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(Form.LocalPath))
        {
            ModelState.AddModelError(string.Empty, "本地路径不能为空。");
        }
        else if (Form.Enabled)
        {
            if (!Directory.Exists(Form.LocalPath))
            {
                ModelState.AddModelError(string.Empty, "启用仓库前，本地路径必须存在。");
            }
            else if (!Directory.Exists(Path.Combine(Form.LocalPath, ".git")))
            {
                ModelState.AddModelError(string.Empty, "启用仓库前，本地路径必须是 Git 仓库。");
            }
        }

        if (Form.MaxPatchFiles is < 1 or > 20)
        {
            ModelState.AddModelError(string.Empty, "单次补丁文件数必须在 1-20 之间。");
        }
    }

    private async Task<IReadOnlyList<PullRequestProfile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        var entities = await db.PullRequestProfiles.AsNoTracking()
            .Where(entity => entity.Enabled)
            .OrderByDescending(entity => entity.IsDefault)
            .ThenBy(entity => entity.Name)
            .ToListAsync(cancellationToken);
        return entities.Select(entity => entity.ToDomain(protector)).ToList();
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public sealed class RepositoryForm
    {
        public Guid? Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string? RemoteUrl { get; set; }
        public string DefaultBranch { get; set; } = "main";
        public string? Instructions { get; set; }
        public string? IncludeGlobsText { get; set; } = string.Empty;
        public string? ExcludeGlobsText { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool AutoReviewEnabled { get; set; }
        public bool AllowAutonomousFix { get; set; }
        public string? ValidationCommand { get; set; }
        public int MaxPatchFiles { get; set; } = 5;
        public string? PullRequestOwner { get; set; }
        public string? PullRequestRepository { get; set; }
        public Guid? PullRequestProfileId { get; set; }
    }
}
