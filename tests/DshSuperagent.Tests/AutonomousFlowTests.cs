using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;

// Integration tests drive the same host queue and workers; no test-only domain bypass.
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DshSuperagent.Tests;

public sealed class AutonomousFlowTests
{
    private const string BuggyFile = "throw new NotImplementedException();\n";
    private const string FixPatch = """
        --- a/Program.cs
        +++ b/Program.cs
        @@ -1 +1 @@
        -throw new NotImplementedException();
        +throw new InvalidOperationException("not supported");
        """;

    [Fact]
    public async Task Scan_AutoFix_Creates_Verified_PullRequest()
    {
        using var factory = new SuperagentTestFactory();
        factory.CreateClient();
        var prProfileId = await IntegrationTestHelpers.CreatePullRequestProfileAsync(factory.Services);
        await IntegrationTestHelpers.CreateLlmProfileAsync(factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "autofix-repo",
            "Program.cs",
            BuggyFile,
            autoReview: true,
            allowFix: true,
            pullRequestProfileId: prProfileId);
        await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");
        factory.Llm.Patch = FixPatch;
        factory.Llm.AssessmentRuleId = await GetRuleIdAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkQueue>()
                .EnqueueAsync(new WorkItem(WorkItemKind.Scan, repositoryId), CancellationToken.None);
        }
        var ticket = await IntegrationTestHelpers.WaitForAsync(
            async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.Tickets.AsNoTracking()
                    .Include(item => item.Reports)
                    .FirstOrDefaultAsync(item => item.RepositoryId == repositoryId
                                                 && item.WorkKind == WorkKind.Fix
                                                 && item.Reports.Any(report => report.PullRequestUrl != null));
            },
            TimeSpan.FromSeconds(30),
            "autonomous fix ticket with PR");

        Assert.Equal(TriggerStrength.Inferred, ticket.Mandate.Strength);
        Assert.Equal(MandateBasis.BotRule, ticket.Mandate.Basis);
        Assert.NotNull(factory.PullRequest.LastRequest);
        var file = Assert.Single(factory.PullRequest.LastRequest!.Files);
        Assert.Equal("Program.cs", file.Path);
        Assert.Contains("InvalidOperationException", file.Content);
        Assert.DoesNotContain("NotImplementedException", file.Content);
    }

    [Fact]
    public async Task AutoFix_Budget_Limits_Tickets_But_Records_All_Findings()
    {
        using var factory = new SuperagentTestFactory();
        factory.CreateClient();
        var prProfileId = await IntegrationTestHelpers.CreatePullRequestProfileAsync(factory.Services);
        await IntegrationTestHelpers.CreateLlmProfileAsync(factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "budget-repo",
            "Program.cs",
            BuggyFile,
            autoReview: true,
            allowFix: true,
            pullRequestProfileId: prProfileId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = new AgentSettings { MaxAutoFixesPerRun = 1 };
            db.Settings.Add(new SettingsEntity
            {
                Id = 1,
                AgentSettingsJson = JsonSerializer.Serialize(settings, ConfigurationMappings.JsonOptions),
                TeamPromptsJson = JsonSerializer.Serialize(new TeamPrompts(), ConfigurationMappings.JsonOptions),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The helper committed only Program.cs; append two more High findings and commit them.
        var repositoryPath = await GetRepositoryPathAsync(factory, repositoryId);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "Z1.cs"), BuggyFile);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "Z2.cs"), BuggyFile);
        await RunGitAsync(repositoryPath, ["add", "."]);
        await RunGitAsync(repositoryPath, ["commit", "-q", "-m", "more findings"]);

        await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");
        factory.Llm.Patch = FixPatch;
        factory.Llm.AssessmentRuleId = await GetRuleIdAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkQueue>()
                .EnqueueAsync(new WorkItem(WorkItemKind.Scan, repositoryId), CancellationToken.None);
        }

        await IntegrationTestHelpers.WaitForAsync(
            async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.Tickets.AsNoTracking()
                    .Include(item => item.Reports)
                    .FirstOrDefaultAsync(item => item.RepositoryId == repositoryId && item.Reports.Any(report => report.PullRequestUrl != null));
            },
            TimeSpan.FromSeconds(30),
            "budgeted fix ticket");

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await verifyDb.Triggers.CountAsync());
        Assert.Equal(1, await verifyDb.Tickets.CountAsync());
        Assert.Equal(2, await verifyDb.Dispositions.CountAsync(item => item.Reason.Contains("自动修复上限")));
    }

    [Fact]
    public async Task Observe_Mode_Records_WouldAccept_Without_Creating_Ticket()
    {
        using var factory = new SuperagentTestFactory();
        factory.CreateClient();
        await IntegrationTestHelpers.CreateLlmProfileAsync(factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "observe-repo",
            "Program.cs",
            BuggyFile,
            autoReview: true,
            allowFix: true);
        await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Observe, "u1");
        factory.Llm.AssessmentRuleId = await GetRuleIdAsync(factory);
        factory.Llm.Patch = FixPatch;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkQueue>()
                .EnqueueAsync(new WorkItem(WorkItemKind.Scan, repositoryId), CancellationToken.None);
        }

        var eval = await IntegrationTestHelpers.WaitForAsync(
            async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var evals = await db.Evals.AsNoTracking().ToListAsync();
                return evals.FirstOrDefault(item => item.Decision?.WouldAccept == true);
            },
            TimeSpan.FromSeconds(20),
            "observe decision with would accept");

        Assert.Equal(EvalStatus.Completed, eval.Status);
        Assert.Equal(DecisionKind.Ignore, eval.Decision!.Kind);
        Assert.True(eval.Decision.WouldAccept);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyDb.Tickets.CountAsync(ticket => ticket.RepositoryId == repositoryId));
        Assert.Null(factory.PullRequest.LastRequest);
    }

    [Fact]
    public async Task Source_Secrets_Are_Encrypted_At_Rest()
    {
        using var factory = new SuperagentTestFactory();
        factory.CreateClient();
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "secret-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Configuration FROM Sources LIMIT 1";
        var raw = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(raw);
        Assert.StartsWith("dp:v1:", raw);
        Assert.DoesNotContain("test-token", raw);
    }

    private static async Task<string> GetRepositoryPathAsync(SuperagentTestFactory factory, Guid repositoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Repositories.AsNoTracking()
            .Where(repository => repository.Id == repositoryId)
            .Select(repository => repository.LocalPath)
            .FirstAsync();
    }

    private static async Task RunGitAsync(string workingDirectory, string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    private static async Task<Guid> GetRuleIdAsync(SuperagentTestFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var policy = await db.Policies.AsNoTracking().Include(item => item.Rules).FirstAsync();
        return policy.Rules.Single().Id;
    }
}
