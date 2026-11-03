using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Web.Data;
using DshSuperagent.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Pages.Admin.Settings;

public sealed class IndexModel(
    AppDbContext db,
    IConfigurationStore configuration,
    SourceProviderRegistry sourceProviders,
    LlmProviderRegistry llmProviders,
    PullRequestProviderRegistry pullRequestProviders,
    AdminCredentials credentials,
    IHostEnvironment environment,
    IConfiguration appConfiguration) : PageModel
{
    [BindProperty]
    public AgentSettings Settings { get; set; } = new();

    [BindProperty]
    public TeamPrompts Prompts { get; set; } = new();

    public string DataDirectory { get; private set; } = string.Empty;
    public string Database { get; private set; } = string.Empty;
    public int SourceProviderCount { get; private set; }
    public int LlmProviderCount { get; private set; }
    public int PullRequestProviderCount { get; private set; }
    public bool IsDefaultPassword { get; private set; }
    public int UnavailableEvals { get; private set; }
    public int FailedMessages { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Settings = await configuration.GetAgentSettingsAsync(cancellationToken);
        Prompts = await configuration.GetTeamPromptsAsync(cancellationToken);
        await LoadHealthAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Settings.MaxAgentIterations = Math.Clamp(Settings.MaxAgentIterations, 1, 16);
        Settings.MaxToolOutputChars = Math.Clamp(Settings.MaxToolOutputChars, 2_000, 40_000);
        Settings.MaxConcurrentAssignments = Math.Clamp(Settings.MaxConcurrentAssignments, 1, 8);
        Settings.ReviewIntervalHours = Math.Clamp(Settings.ReviewIntervalHours, 1, 168);
        Settings.MaxReviewFiles = Math.Clamp(Settings.MaxReviewFiles, 1, 2_000);
        Settings.PatchValidationTimeoutSeconds = Math.Clamp(Settings.PatchValidationTimeoutSeconds, 10, 900);
        Settings.MaxAutoFixesPerRun = Math.Clamp(Settings.MaxAutoFixesPerRun, 1, 5);
        var entity = await db.Settings.FirstOrDefaultAsync(settings => settings.Id == 1, cancellationToken);
        if (entity is null)
        {
            entity = new SettingsEntity { Id = 1 };
            await db.Settings.AddAsync(entity, cancellationToken);
        }

        entity.AgentSettingsJson = JsonSerializer.Serialize(Settings, ConfigurationMappings.JsonOptions);
        entity.TeamPromptsJson = JsonSerializer.Serialize(Prompts, ConfigurationMappings.JsonOptions);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "运行设置与团队提示词已保存。并发/间隔设置对新建的 worker 生效；修改定时审查间隔建议重启服务。";
        return RedirectToPage();
    }

    private async Task LoadHealthAsync(CancellationToken cancellationToken)
    {
        DataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Database = appConfiguration.GetConnectionString("Superagent")
                   ?? $"Data Source={Path.Combine(DataDirectory, "superagent.db")}";
        SourceProviderCount = sourceProviders.All.Count;
        LlmProviderCount = llmProviders.All.Count;
        PullRequestProviderCount = pullRequestProviders.All.Count;
        IsDefaultPassword = credentials.IsDefaultPassword;
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        UnavailableEvals = await db.Evals.CountAsync(
            eval => eval.Status == Domain.Authorization.EvalStatus.Unavailable && eval.CompletedAt >= since,
            cancellationToken);
        FailedMessages = await db.ChannelMessages.CountAsync(
            message => message.Status == MessageStatus.Dropped || (message.Status == MessageStatus.Failed && message.Attempts >= 3),
            cancellationToken);
    }
}
