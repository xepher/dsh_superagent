using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Providers.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DshSuperagent.Tests;

public sealed class SuperagentTestFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dsh-superagent-test-{Guid.NewGuid():N}.db");

    public FakeLlmProvider Llm { get; } = new();
    public FakePullRequestProvider PullRequest { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Superagent", $"Data Source={_databasePath}");
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services
                         .Where(service => service.ServiceType == typeof(ILlmProvider)
                                           || service.ServiceType == typeof(IPullRequestProvider))
                         .ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<ILlmProvider>(Llm);
            services.AddSingleton<IPullRequestProvider>(PullRequest);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class FakeLlmProvider : ILlmProvider
{
    public string Id => "fake-llm";
    public string DisplayName => "Fake LLM";
    public string Description => "Test double";
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];

    public Guid AssessmentRuleId { get; set; }
    public string FinalAnswer { get; set; } = "模拟模型回答：关键逻辑在仓库文件中。";
    public string Patch { get; set; } = string.Empty;
    public int Calls { get; private set; }
    public List<string> Prompts { get; } = [];

    public Task<LlmChatResponse> ChatAsync(LlmProfile profile, LlmChatRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var system = request.Messages.FirstOrDefault(message => message.Role == "system")?.Content ?? string.Empty;
        var user = request.Messages.LastOrDefault(message => message.Role == "user")?.Content ?? string.Empty;
        Prompts.Add(system + "\n" + user);
        if (system.Contains("Eval 研判层", StringComparison.Ordinal))
        {
            var json = JsonSerializer.Serialize(new
            {
                accept = true,
                rule_ids = new[] { AssessmentRuleId },
                reason = "测试研判：命中代码质量规则。",
                scope = "fix",
            });
            return Task.FromResult(new LlmChatResponse(json, []));
        }

        if (user.Contains("修复补丁", StringComparison.Ordinal))
        {
            var json = JsonSerializer.Serialize(new
            {
                diff = Patch,
                explanation = "测试修复：替换未实现异常。",
                risk = "低；仅改变异常类型。",
            });
            return Task.FromResult(new LlmChatResponse(json, []));
        }

        return Task.FromResult(new LlmChatResponse(FinalAnswer, []));
    }

    public Task<ProviderCheckResult> CheckAsync(LlmProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCheckResult(true, "fake ok"));
}

public sealed class FakePullRequestProvider : IPullRequestProvider
{
    public string Id => "fake-pr";
    public string DisplayName => "Fake PR";
    public string Description => "Test double";
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];
    public PullRequestRequest? LastRequest { get; private set; }

    public Task<PullRequestResult> CreateAsync(
        PullRequestProfile profile,
        PullRequestRequest request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new PullRequestResult(true, "https://github.com/acme/demo/pull/7", "7", "created"));
    }

    public Task<ProviderCheckResult> CheckAsync(PullRequestProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCheckResult(true, "fake pr ok"));
}
