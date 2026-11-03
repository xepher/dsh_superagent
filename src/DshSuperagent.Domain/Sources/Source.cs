using DshSuperagent.Domain.Common;

namespace DshSuperagent.Domain.Sources;

/// <summary>
/// A configured stream of events. The provider is an adapter (WeCom, scheduled repository scan, ...);
/// the domain only knows that a Source has an identity, a read position and provider-neutral settings.
/// </summary>
public sealed class Source : AggregateRoot
{
    private readonly Dictionary<string, string> _configuration = new(StringComparer.OrdinalIgnoreCase);

    private Source()
    {
    }

    private Source(Guid id, string providerId, string name, bool enabled, Dictionary<string, string> configuration, Guid? defaultRepositoryId)
    {
        Id = id;
        ProviderId = providerId;
        Name = name;
        Enabled = enabled;
        _configuration = new Dictionary<string, string>(configuration, StringComparer.OrdinalIgnoreCase);
        DefaultRepositoryId = defaultRepositoryId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public string ProviderId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public IReadOnlyDictionary<string, string> Configuration => _configuration;
    public Guid? DefaultRepositoryId { get; private set; }

    /// <summary>The Source's own read position. A restart must not replay events.</summary>
    public string? ReadPosition { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Source Create(
        string providerId,
        string name,
        Dictionary<string, string> configuration,
        Guid? defaultRepositoryId = null,
        bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new DomainException("Source 必须指定 Provider。");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Source 名称不能为空。");
        }

        return new Source(Guid.NewGuid(), providerId.Trim(), name.Trim(), enabled, configuration, defaultRepositoryId);
    }

    public string? GetConfig(string key) => _configuration.TryGetValue(key, out var value) ? value : null;

    public void Configure(string name, Dictionary<string, string> configuration, Guid? defaultRepositoryId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Source 名称不能为空。");
        }

        Name = name.Trim();
        _configuration.Clear();
        foreach (var pair in configuration)
        {
            _configuration[pair.Key] = pair.Value;
        }

        DefaultRepositoryId = defaultRepositoryId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordReadPosition(string position)
    {
        ReadPosition = position;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public enum AcceptMode
{
    Off,
    Observe,
    Auto,
}

public enum AdmissionChoice
{
    Ignore,
    Inferred,
    Explicit,
}

public enum TriggerStrength
{
    NotAdmitted,
    Inferred,
    Explicit,
}

public enum GateBasis
{
    ExplicitMention,
    ExplicitAdmissionCell,
    FollowUpListed,
    FollowUpUnlisted,
    AdmissionCell,
    BotCandidate,
    Ignored,
}

public sealed record AllowedUser(string UserId, string DisplayName = "");

/// <summary>
/// The three admission cells from the prototype. They are part of the Policy version, not Source configuration.
/// </summary>
public sealed class AdmissionTable
{
    private AdmissionTable()
    {
    }

    public AdmissionTable(
        AdmissionChoice listedNotAddressed = AdmissionChoice.Inferred,
        AdmissionChoice unlistedAddressed = AdmissionChoice.Ignore,
        AdmissionChoice unlistedNotAddressed = AdmissionChoice.Ignore)
    {
        ListedNotAddressed = listedNotAddressed;
        UnlistedAddressed = unlistedAddressed;
        UnlistedNotAddressed = unlistedNotAddressed;
    }

    public AdmissionChoice ListedNotAddressed { get; private set; }
    public AdmissionChoice UnlistedAddressed { get; private set; }
    public AdmissionChoice UnlistedNotAddressed { get; private set; }

    public AdmissionChoice CellFor(bool isListed, bool addressed) => (isListed, addressed) switch
    {
        (true, false) => ListedNotAddressed,
        (false, true) => UnlistedAddressed,
        (false, false) => UnlistedNotAddressed,
        _ => AdmissionChoice.Ignore,
    };
}

public sealed class SourcePolicy
{
    private readonly List<AllowedUser> _allowedUsers = [];

    private SourcePolicy()
    {
    }

    public SourcePolicy(Guid sourceId)
    {
        SourceId = sourceId;
        Admission = new AdmissionTable();
    }

    public Guid SourceId { get; private set; }
    public List<AllowedUser> AllowedUsers => _allowedUsers;
    public List<string> BotIds { get; private set; } = [];
    public AdmissionTable Admission { get; private set; } = new();

    /// <summary>Whether the Source receives messages that do not address the agent (e.g. a group set to all messages).</summary>
    public bool ReceiveUnaddressed { get; private set; }

    public void Update(
        IEnumerable<AllowedUser> allowedUsers,
        IEnumerable<string> botIds,
        AdmissionTable admission,
        bool receiveUnaddressed)
    {
        _allowedUsers.Clear();
        _allowedUsers.AddRange(allowedUsers
            .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
            .GroupBy(user => user.UserId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AllowedUser(group.Key, group.First().DisplayName.Trim())));
        BotIds = botIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Admission = admission;
        ReceiveUnaddressed = receiveUnaddressed;
    }

    public bool IsListed(string userId) =>
        _allowedUsers.Any(user => string.Equals(user.UserId, userId, StringComparison.OrdinalIgnoreCase));

    public bool IsListedBot(string actorId) =>
        BotIds.Any(id => string.Equals(id, actorId, StringComparison.OrdinalIgnoreCase));
}

public sealed record GateResult(
    TriggerStrength Strength,
    GateBasis Basis,
    string Reason)
{
    public bool IsAdmitted => Strength != TriggerStrength.NotAdmitted;
}
