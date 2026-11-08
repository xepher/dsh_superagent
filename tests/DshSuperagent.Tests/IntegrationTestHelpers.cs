using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.WeCom;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DshSuperagent.Tests;

public static class IntegrationTestHelpers
{
    public static string NewAesKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');

    public static async Task<Guid> CreateRepositoryAsync(
        IServiceProvider services,
        string slug,
        string filePath,
        string content,
        bool autoReview = false,
        bool allowFix = false,
        Guid? pullRequestProfileId = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dsh-superagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, filePath))!);
        await File.WriteAllTextAsync(Path.Combine(directory, filePath), content);
        await RunGitAsync(directory, ["init", "-q"]);
        await RunGitAsync(directory, ["config", "user.email", "test@example.invalid"]);
        await RunGitAsync(directory, ["config", "user.name", "Test"]);
        await RunGitAsync(directory, ["add", "."]);
        await RunGitAsync(directory, ["commit", "-q", "-m", "init"]);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = new RepositoryProfile
        {
            Slug = slug,
            DisplayName = slug,
            LocalPath = directory,
            RemoteUrl = "https://github.com/acme/demo.git",
            DefaultBranch = "master",
            Enabled = true,
            AutoReviewEnabled = autoReview,
            AllowAutonomousFix = allowFix,
            PullRequestOwner = "acme",
            PullRequestRepository = "demo",
            PullRequestProfileId = pullRequestProfileId,
            MaxPatchFiles = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var entity = new RepositoryEntity();
        entity.Apply(repository);
        db.Repositories.Add(entity);
        await db.SaveChangesAsync();
        return repository.Id;
    }

    public static async Task<Guid> CreateSourceAndPolicyAsync(
        IServiceProvider services,
        Guid repositoryId,
        AcceptMode acceptMode = AcceptMode.Auto,
        params string[] allowedUsers)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = Source.Create(
            WeComAibotSourceProvider.ProviderId,
            "测试企业微信",
            new Dictionary<string, string>
            {
                ["token"] = "test-token",
                ["encodingAesKey"] = NewAesKey(),
                ["botName"] = "测试机器人",
                ["replyMode"] = "stream",
            },
            repositoryId);
        db.Sources.Add(source);
        var policy = await db.Policies.Include(item => item.Rules).Include(item => item.SourcePolicies)
            .FirstAsync();
        policy.SetAcceptMode(acceptMode);
        policy.UpdateSourcePolicy(
            source.Id,
            allowedUsers.Select(user => new AllowedUser(user, user)).ToList(),
            [],
            new AdmissionTable(),
            false);
        await db.SaveChangesAsync();
        return source.Id;
    }

    public static async Task<Guid> CreateLlmProfileAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var entity = new LlmProfileEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = "fake-llm",
            Name = "fake-llm",
            Enabled = true,
            IsDefault = true,
            ConfigJson = protector.Protect("{}"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmProfiles.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    public static async Task<Guid> CreatePullRequestProfileAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var entity = new PullRequestProfileEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = "fake-pr",
            Name = "fake-pr",
            Enabled = true,
            IsDefault = true,
            ConfigJson = protector.Protect("""{"dryRun":"false"}"""),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PullRequestProfiles.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    public static async Task<CallbackAck> SendMessageAsync(
        TestClient client,
        Guid sourceId,
        string userId,
        string text,
        string? messageId = null)
    {
        var inner = new JsonObject
        {
            ["msgid"] = messageId ?? Guid.NewGuid().ToString("N"),
            ["chattype"] = "single",
            ["from"] = new JsonObject { ["userid"] = userId },
            ["msgtype"] = "text",
            ["text"] = new JsonObject { ["content"] = text },
        };
        return await SendEncryptedAsync(client, sourceId, inner);
    }

    public static async Task<string> SendRawMessageAsync(
        TestClient client,
        Guid sourceId,
        string userId,
        string text)
    {
        var inner = new JsonObject
        {
            ["msgid"] = Guid.NewGuid().ToString("N"),
            ["chattype"] = "single",
            ["from"] = new JsonObject { ["userid"] = userId },
            ["msgtype"] = "text",
            ["text"] = new JsonObject { ["content"] = text },
        };
        return await PostEncryptedAsync(client, sourceId, inner);
    }

    public static async Task<CallbackAck> SendGroupMessageAsync(
        TestClient client,
        Guid sourceId,
        string userId,
        string chatId,
        string text)
    {
        var inner = new JsonObject
        {
            ["msgid"] = Guid.NewGuid().ToString("N"),
            ["chattype"] = "group",
            ["chatid"] = chatId,
            ["from"] = new JsonObject { ["userid"] = userId },
            ["msgtype"] = "text",
            ["text"] = new JsonObject { ["content"] = text },
        };
        return await SendEncryptedAsync(client, sourceId, inner);
    }

    public static async Task<CallbackAck> SendStreamRefreshAsync(TestClient client, Guid sourceId, string streamId)
    {
        var inner = new JsonObject
        {
            ["msgid"] = Guid.NewGuid().ToString("N"),
            ["chattype"] = "single",
            ["from"] = new JsonObject { ["userid"] = "u1" },
            ["msgtype"] = "stream",
            ["stream"] = new JsonObject { ["id"] = streamId },
        };
        return await SendEncryptedAsync(client, sourceId, inner);
    }

    public static async Task<string> VerifyUrlAsync(TestClient client, Guid sourceId, string plaintext)
    {
        await using var scope = client.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstAsync(item => item.Id == sourceId);
        var token = source.GetConfig("token")!;
        var key = source.GetConfig("encodingAesKey")!;
        var encrypted = WeComCrypto.Encrypt(key, plaintext);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var signature = WeComCrypto.ComputeSignature(token, timestamp, nonce, encrypted);
        var response = await client.Http.GetAsync(
            $"/api/sources/{sourceId}/callback?msg_signature={signature}&timestamp={timestamp}&nonce={nonce}&echostr={Uri.EscapeDataString(encrypted)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<StreamPoll> WaitForStreamAsync(
        TestClient client,
        Guid sourceId,
        string streamId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ack = await SendStreamRefreshAsync(client, sourceId, streamId);
            if (ack.Stream is not null && ack.Stream.Finished)
            {
                return ack.Stream;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"stream {streamId} 未在 {timeout} 内完成");
    }

    public static async Task<T> WaitForAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        string description)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"等待 {description} 超时");
    }

    private static async Task<CallbackAck> SendEncryptedAsync(TestClient client, Guid sourceId, JsonObject inner)
    {
        var outer = await PostEncryptedAsync(client, sourceId, inner);
        if (string.IsNullOrWhiteSpace(outer))
        {
            return new CallbackAck(null);
        }

        await using var scope = client.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstAsync(item => item.Id == sourceId);
        var key = source.GetConfig("encodingAesKey")!;
        using var document = JsonDocument.Parse(outer);
        if (!document.RootElement.TryGetProperty("encrypt", out var encryptedProperty))
        {
            return new CallbackAck(null);
        }

        var plain = WeComCrypto.Decrypt(key, encryptedProperty.GetString()!);
        using var ack = JsonDocument.Parse(plain);
        if (ack.RootElement.TryGetProperty("stream", out var stream))
        {
            return new CallbackAck(new StreamPoll(
                stream.GetProperty("id").GetString()!,
                stream.TryGetProperty("finish", out var finish) && finish.GetBoolean(),
                stream.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty));
        }

        return new CallbackAck(null);
    }

    private static async Task<string> PostEncryptedAsync(TestClient client, Guid sourceId, JsonObject inner)
    {
        await using var scope = client.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstAsync(item => item.Id == sourceId);
        var token = source.GetConfig("token")!;
        var key = source.GetConfig("encodingAesKey")!;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var encrypted = WeComCrypto.Encrypt(key, inner.ToJsonString());
        var signature = WeComCrypto.ComputeSignature(token, timestamp, nonce, encrypted);
        var response = await client.Http.PostAsync(
            $"/api/sources/{sourceId}/callback?msg_signature={signature}&timestamp={timestamp}&nonce={nonce}",
            new StringContent(JsonSerializer.Serialize(new { encrypt = encrypted }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task RunGitAsync(string workingDirectory, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
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

    public sealed record TestClient(HttpClient Http, IServiceProvider Services);
    public sealed record CallbackAck(StreamPoll? Stream);
    public sealed record StreamPoll(string Id, bool Finished, string Content);
}
