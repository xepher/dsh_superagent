using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;

namespace DshSuperagent.Web.Data;

public static class ConfigurationMappings
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RepositoryProfile ToDomain(this RepositoryEntity entity) => new()
    {
        Id = entity.Id,
        Slug = entity.Slug,
        DisplayName = entity.DisplayName,
        LocalPath = entity.LocalPath,
        RemoteUrl = entity.RemoteUrl,
        DefaultBranch = entity.DefaultBranch,
        Instructions = entity.Instructions,
        IncludeGlobs = JsonSerializer.Deserialize<List<string>>(entity.IncludeGlobsJson, JsonOptions) ?? [],
        ExcludeGlobs = JsonSerializer.Deserialize<List<string>>(entity.ExcludeGlobsJson, JsonOptions) ?? [],
        Enabled = entity.Enabled,
        AutoReviewEnabled = entity.AutoReviewEnabled,
        AllowAutonomousFix = entity.AllowAutonomousFix,
        ValidationCommand = entity.ValidationCommand,
        MaxPatchFiles = entity.MaxPatchFiles,
        PullRequestOwner = entity.PullRequestOwner,
        PullRequestRepository = entity.PullRequestRepository,
        PullRequestProfileId = entity.PullRequestProfileId,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    public static void Apply(this RepositoryEntity entity, RepositoryProfile repository)
    {
        entity.Id = repository.Id;
        entity.Slug = repository.Slug;
        entity.DisplayName = repository.DisplayName;
        entity.LocalPath = repository.LocalPath;
        entity.RemoteUrl = repository.RemoteUrl;
        entity.DefaultBranch = repository.DefaultBranch;
        entity.Instructions = repository.Instructions;
        entity.IncludeGlobsJson = JsonSerializer.Serialize(repository.IncludeGlobs, JsonOptions);
        entity.ExcludeGlobsJson = JsonSerializer.Serialize(repository.ExcludeGlobs, JsonOptions);
        entity.Enabled = repository.Enabled;
        entity.AutoReviewEnabled = repository.AutoReviewEnabled;
        entity.AllowAutonomousFix = repository.AllowAutonomousFix;
        entity.ValidationCommand = repository.ValidationCommand;
        entity.MaxPatchFiles = repository.MaxPatchFiles;
        entity.PullRequestOwner = repository.PullRequestOwner;
        entity.PullRequestRepository = repository.PullRequestRepository;
        entity.PullRequestProfileId = repository.PullRequestProfileId;
        entity.CreatedAt = repository.CreatedAt;
        entity.UpdatedAt = repository.UpdatedAt;
    }

    public static LlmProfile ToDomain(this LlmProfileEntity entity, ISecretProtector protector) => new()
    {
        Id = entity.Id,
        ProviderId = entity.ProviderId,
        Name = entity.Name,
        Enabled = entity.Enabled,
        Configuration = DeserializeConfig(protector.Unprotect(entity.ConfigJson)),
        IsDefault = entity.IsDefault,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    public static void Apply(this LlmProfileEntity entity, LlmProfile profile, ISecretProtector protector)
    {
        entity.Id = profile.Id;
        entity.ProviderId = profile.ProviderId;
        entity.Name = profile.Name;
        entity.Enabled = profile.Enabled;
        entity.ConfigJson = protector.Protect(JsonSerializer.Serialize(profile.Configuration, JsonOptions));
        entity.IsDefault = profile.IsDefault;
        entity.CreatedAt = profile.CreatedAt;
        entity.UpdatedAt = profile.UpdatedAt;
    }

    public static PullRequestProfile ToDomain(this PullRequestProfileEntity entity, ISecretProtector protector) => new()
    {
        Id = entity.Id,
        ProviderId = entity.ProviderId,
        Name = entity.Name,
        Enabled = entity.Enabled,
        Configuration = DeserializeConfig(protector.Unprotect(entity.ConfigJson)),
        IsDefault = entity.IsDefault,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    public static void Apply(this PullRequestProfileEntity entity, PullRequestProfile profile, ISecretProtector protector)
    {
        entity.Id = profile.Id;
        entity.ProviderId = profile.ProviderId;
        entity.Name = profile.Name;
        entity.Enabled = profile.Enabled;
        entity.ConfigJson = protector.Protect(JsonSerializer.Serialize(profile.Configuration, JsonOptions));
        entity.IsDefault = profile.IsDefault;
        entity.CreatedAt = profile.CreatedAt;
        entity.UpdatedAt = profile.UpdatedAt;
    }

    private static Dictionary<string, string> DeserializeConfig(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }
}
