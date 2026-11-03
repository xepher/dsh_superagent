using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Data;

/// <summary>Read-side adapter for deployment configuration. Domain state lives in Sources and Policy, not in these tables.</summary>
public sealed class EfConfigurationStore(AppDbContext db, ISecretProtector protector) : IConfigurationStore
{
    public async Task<RepositoryProfile?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(repository => repository.Id == id, cancellationToken);
        return entity?.ToDomain();
    }

    public async Task<RepositoryProfile?> GetRepositoryBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var entity = await db.Repositories.AsNoTracking()
            .FirstOrDefaultAsync(repository => repository.Slug.ToLower() == normalized, cancellationToken);
        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<RepositoryProfile>> GetRepositoriesAsync(bool enabledOnly, CancellationToken cancellationToken)
    {
        var query = db.Repositories.AsNoTracking();
        if (enabledOnly)
        {
            query = query.Where(repository => repository.Enabled);
        }

        var entities = await query.OrderBy(repository => repository.DisplayName).ToListAsync(cancellationToken);
        return entities.Select(entity => entity.ToDomain()).ToList();
    }

    public async Task<LlmProfile?> GetDefaultLlmAsync(CancellationToken cancellationToken)
    {
        var entity = await db.LlmProfiles.AsNoTracking()
            .Where(profile => profile.Enabled)
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity?.ToDomain(protector);
    }

    public async Task<PullRequestProfile?> GetDefaultPullRequestAsync(CancellationToken cancellationToken)
    {
        var entity = await db.PullRequestProfiles.AsNoTracking()
            .Where(profile => profile.Enabled)
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity?.ToDomain(protector);
    }

    public async Task<PullRequestProfile?> GetPullRequestProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.PullRequestProfiles.AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.Id == id && profile.Enabled, cancellationToken);
        return entity?.ToDomain(protector);
    }

    public async Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken)
    {
        var entity = await db.Settings.AsNoTracking().FirstOrDefaultAsync(settings => settings.Id == 1, cancellationToken);
        return entity is null
            ? new AgentSettings()
            : Deserialize(entity.AgentSettingsJson, new AgentSettings());
    }

    public async Task<TeamPrompts> GetTeamPromptsAsync(CancellationToken cancellationToken)
    {
        var entity = await db.Settings.AsNoTracking().FirstOrDefaultAsync(settings => settings.Id == 1, cancellationToken);
        return entity is null
            ? new TeamPrompts()
            : Deserialize(entity.TeamPromptsJson, new TeamPrompts());
    }

    private static T Deserialize<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ConfigurationMappings.JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
