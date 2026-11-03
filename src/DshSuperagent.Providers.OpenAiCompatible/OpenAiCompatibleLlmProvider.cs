using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.Sources;

namespace DshSuperagent.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
{
    public const string ProviderId = "openai-compatible";

    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiCompatibleLlmProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Id => ProviderId;
    public string DisplayName => "OpenAI 兼容模型";
    public string Description => "任何兼容 /chat/completions 与 function calling 的模型服务，例如 DeepSeek、Qwen、Ollama。";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new("endpoint", "API 地址", ProviderFieldType.Text, Required: true,
            Placeholder: "https://api.deepseek.com/v1",
            Help: "填写到 /v1 为止；系统会自动追加 /chat/completions。"),
        new("apiKey", "API Key", ProviderFieldType.Secret,
            Help: "本地 Ollama 可留空。密钥会加密存储，不会下发到模型或日志。"),
        new("model", "模型名称", ProviderFieldType.Text, Required: true,
            Placeholder: "deepseek-chat", Help: "服务端实际支持的模型 ID。"),
        new("organization", "Organization（可选）", ProviderFieldType.Text),
        new("timeoutSeconds", "请求超时（秒）", ProviderFieldType.Number, DefaultValue: "120"),
    ];

    public async Task<LlmChatResponse> ChatAsync(
        LlmProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        var settings = ReadSettings(profile);
        var payload = BuildPayload(settings, request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(settings.Organization))
        {
            httpRequest.Headers.TryAddWithoutValidation("OpenAI-Organization", settings.Organization);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var client = _httpClientFactory.CreateClient("llm");
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"模型服务返回 HTTP {(int)response.StatusCode}：{ExtractError(body)}");
        }

        return ParseResponse(body);
    }

    public async Task<ProviderCheckResult> CheckAsync(LlmProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var settings = ReadSettings(profile);
            var payload = new JsonObject
            {
                ["model"] = settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word: ok" },
                },
                ["max_tokens"] = 8,
                ["temperature"] = 0,
                ["stream"] = false,
            };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(settings.TimeoutSeconds, 30)));
            var client = _httpClientFactory.CreateClient("llm");
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderCheckResult(false, $"模型服务返回 HTTP {(int)response.StatusCode}。", ExtractError(body));
            }

            var parsed = ParseResponse(body);
            return new ProviderCheckResult(
                true,
                $"连接成功，模型返回：{Trim(parsed.Content ?? "（空内容）", 80)}",
                $"模型：{settings.Model}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderCheckResult(false, "模型连接失败。", ex.Message);
        }
    }

    private static JsonObject BuildPayload(LlmSettings settings, LlmChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var json = new JsonObject { ["role"] = message.Role };
            if (message.Content is not null)
            {
                json["content"] = message.Content;
            }

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson,
                        },
                    });
                }

                json["tool_calls"] = calls;
            }

            if (message.Role == "tool")
            {
                json["tool_call_id"] = message.ToolCallId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(message.Name))
                {
                    json["name"] = message.Name;
                }
            }

            messages.Add(json);
        }

        var payload = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                JsonNode parameters;
                try
                {
                    parameters = JsonNode.Parse(tool.ParametersJson) ?? new JsonObject();
                }
                catch (JsonException)
                {
                    parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = parameters,
                    },
                });
            }

            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    private static LlmChatResponse ParseResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("模型响应缺少 choices。");
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null;
        if (!choice.TryGetProperty("message", out var message))
        {
            throw new InvalidOperationException("模型响应缺少 message。");
        }

        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() : Guid.NewGuid().ToString("N");
                if (!call.TryGetProperty("function", out var function))
                {
                    continue;
                }

                var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var arguments = function.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement.GetString()
                    : "{}";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    calls.Add(new LlmToolCall(id ?? Guid.NewGuid().ToString("N"), name, arguments ?? "{}"));
                }
            }
        }

        int? promptTokens = null;
        int? completionTokens = null;
        if (document.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue))
            {
                promptTokens = promptValue;
            }

            if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionValue))
            {
                completionTokens = completionValue;
            }
        }

        return new LlmChatResponse(content, calls, finishReason, promptTokens, completionTokens);
    }

    private static LlmSettings ReadSettings(LlmProfile profile)
    {
        var endpointText = profile.GetConfig("endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("模型 API 地址必须是合法的 HTTP(S) 地址。");
        }

        var model = profile.GetConfig("model");
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("模型名称不能为空。");
        }

        var timeout = int.TryParse(profile.GetConfig("timeoutSeconds"), out var seconds)
            ? Math.Clamp(seconds, 10, 600)
            : 120;
        var chatEndpoint = new Uri(endpoint.ToString().TrimEnd('/') + "/chat/completions");
        return new LlmSettings(
            chatEndpoint,
            profile.GetConfig("apiKey") ?? string.Empty,
            model.Trim(),
            profile.GetConfig("organization"),
            timeout);
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                {
                    return Trim(message.GetString() ?? body, 400);
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return Trim(error.GetString() ?? body, 400);
                }
            }
        }
        catch (JsonException)
        {
        }

        return Trim(body, 400);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record LlmSettings(
        Uri Endpoint,
        string ApiKey,
        string Model,
        string? Organization,
        int TimeoutSeconds);
}
