using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Sources;

namespace DshSuperagent.Core.Providers.Llm;

public sealed record LlmToolDefinition(string Name, string Description, string ParametersJson);

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed record LlmMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);

public sealed record LlmChatRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition>? Tools,
    double Temperature,
    int MaxTokens);

public sealed record LlmChatResponse(
    string? Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string? FinishReason = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

public interface ILlmProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    Task<LlmChatResponse> ChatAsync(
        LlmProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken);

    Task<ProviderCheckResult> CheckAsync(
        LlmProfile profile,
        CancellationToken cancellationToken);
}

public sealed class LlmProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    public LlmProviderRegistry(IEnumerable<ILlmProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ILlmProvider> All => _providers.Values;

    public ILlmProvider? Find(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;
}
