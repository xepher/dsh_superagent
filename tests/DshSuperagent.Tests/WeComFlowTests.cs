using DshSuperagent.Core.Application;
using DshSuperagent.Core.Storage;
using DshSuperagent.Providers.WeCom;
using System.Text.Json;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DshSuperagent.Tests;

public sealed class WeComFlowTests
{
    [Fact]
    public async Task Url_Verification_Decrypts_Echo()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "verify-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var echo = await IntegrationTestHelpers.VerifyUrlAsync(client, sourceId, "challenge-123");

        Assert.Equal("challenge-123", echo);
    }

    [Fact]
    public async Task Explicit_Question_Creates_Ticket_And_Streams_Answer()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "question-repo",
            "Docs/Page.cs",
            "// 用户列表页的分页逻辑在 QueryObject 中实现\npublic sealed class QueryObject { }\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var ack = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "用户列表页的分页逻辑在哪里？", "wecom-msg-1");
        Assert.NotNull(ack.Stream);

        var final = await IntegrationTestHelpers.WaitForStreamAsync(
            client,
            sourceId,
            ack.Stream!.Id,
            TimeSpan.FromSeconds(25));

        Assert.True(final.Finished);
        Assert.Contains("Docs/Page.cs", final.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("分页", final.Content);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tickets = await db.Tickets.AsNoTracking().Include(ticket => ticket.Reports).ToListAsync();
        Assert.Single(tickets);
        Assert.Equal(TicketStatus.Ended, tickets[0].Status);
        Assert.Equal(TicketOutcome.Completed, tickets[0].Outcome);
        Assert.Equal(WorkKind.Answer, tickets[0].WorkKind);
        Assert.Equal(TriggerStrength.Explicit, tickets[0].Mandate.Strength);
    }

    [Fact]
    public async Task Question_Text_Containing_Fix_Command_Is_Still_An_Answer()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "fix-word-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var ack = await IntegrationTestHelpers.SendMessageAsync(
            client,
            sourceId,
            "u1",
            "文档里写的 /fix 是什么意思？");
        await IntegrationTestHelpers.WaitForStreamAsync(client, sourceId, ack.Stream!.Id, TimeSpan.FromSeconds(25));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.Equal(WorkKind.Answer, ticket.WorkKind);
    }

    [Fact]
    public async Task Duplicate_Callback_Does_Not_Create_Second_Ticket()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "duplicate-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var first = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "分页逻辑在哪里？", "same-message");
        var second = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "分页逻辑在哪里？", "same-message");
        Assert.NotNull(first.Stream);
        Assert.Equal(first.Stream!.Id, second.Stream!.Id);

        await IntegrationTestHelpers.WaitForStreamAsync(client, sourceId, first.Stream.Id, TimeSpan.FromSeconds(25));
        await Task.Delay(500);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Tickets.CountAsync());
        Assert.Equal(1, await db.Triggers.CountAsync());
    }

    [Fact]
    public async Task Group_Message_Requires_Mention()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "group-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var ignored = await IntegrationTestHelpers.SendGroupMessageAsync(
            client,
            sourceId,
            "u1",
            "chat-1",
            "分页逻辑在哪里？");
        Assert.Null(ignored.Stream);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.Triggers.CountAsync());
        }

        var addressed = await IntegrationTestHelpers.SendGroupMessageAsync(
            client,
            sourceId,
            "u1",
            "chat-1",
            "@测试机器人 分页逻辑在哪里？");
        Assert.NotNull(addressed.Stream);

        await using (var verifyScope = factory.Services.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trigger = await db.Triggers.AsNoTracking().SingleAsync();
            Assert.True(trigger.Addressed);
            Assert.Equal("分页逻辑在哪里？", trigger.Text);
            Assert.Equal("wecom:chat:chat-1", (await db.Threads.AsNoTracking().SingleAsync()).ExternalKey);
        }
    }

    [Fact]
    public async Task Unauthorized_FollowUp_Cannot_Drive_Existing_Ticket()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "followup-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var first = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "分页逻辑在哪里？");
        await IntegrationTestHelpers.WaitForStreamAsync(client, sourceId, first.Stream!.Id, TimeSpan.FromSeconds(25));

        var followUp = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u2", "继续解释一下这个逻辑");
        var final = await IntegrationTestHelpers.WaitForStreamAsync(
            client,
            sourceId,
            followUp.Stream!.Id,
            TimeSpan.FromSeconds(15));

        Assert.Contains("未在 Source 的允许列表中", final.Content);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await db.Tickets.AsNoTracking()
            .Include(item => item.Inputs)
            .Include(item => item.Assignments)
            .SingleAsync();
        Assert.Single(ticket.Inputs);
        Assert.Single(ticket.Assignments);
        Assert.Equal(1, await db.Tickets.CountAsync());
    }

    [Fact]
    public async Task Unauthorized_User_Gets_Notice_And_No_Ticket()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "unauthorized-repo",
            "Docs/Page.cs",
            "// 分页逻辑\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var ack = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u2", "分页逻辑在哪里？");
        Assert.NotNull(ack.Stream);
        var final = await IntegrationTestHelpers.WaitForStreamAsync(
            client,
            sourceId,
            ack.Stream!.Id,
            TimeSpan.FromSeconds(15));

        Assert.True(final.Finished);
        Assert.Contains("未在 Source 的允许列表中", final.Content);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Tickets.CountAsync());
        Assert.Equal(0, await db.Evals.CountAsync());
        Assert.Equal(1, await db.Triggers.CountAsync());
    }

    [Fact]
    public async Task Encrypted_Ack_Is_Raw_Utf8_Without_Json_Escapes()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(
            factory.Services,
            await IntegrationTestHelpers.CreateRepositoryAsync(factory.Services, "escape-repo", "Docs/Page.cs", "// 分页逻辑\n"),
            AcceptMode.Auto,
            "u1");

        var outer = await IntegrationTestHelpers.SendRawMessageAsync(client, sourceId, "u1", "/help");

        Assert.DoesNotContain("\\u", outer);
        using var envelope = JsonDocument.Parse(outer);
        var cipher = envelope.RootElement.GetProperty("encrypt").GetString()!;
        var nonce = envelope.RootElement.GetProperty("nonce").GetString()!;
        var timestamp = envelope.RootElement.GetProperty("timestamp").ToString();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstAsync(item => item.Id == sourceId);
        var token = source.GetConfig("token")!;
        var plain = WeComCrypto.Decrypt(source.GetConfig("encodingAesKey")!, cipher);

        Assert.Equal(
            WeComCrypto.ComputeSignature(token, timestamp, nonce, cipher),
            envelope.RootElement.GetProperty("msgsignature").GetString());
        Assert.DoesNotContain("\\u", plain);
        using var ack = JsonDocument.Parse(plain);
        Assert.Equal("stream", ack.RootElement.GetProperty("msgtype").GetString());
    }

    [Fact]
    public async Task Second_Question_In_Same_Thread_Is_Answered_On_Its_Own_Stream()
    {
        using var factory = new SuperagentTestFactory();
        var http = factory.CreateClient();
        var client = new IntegrationTestHelpers.TestClient(http, factory.Services);
        var repositoryId = await IntegrationTestHelpers.CreateRepositoryAsync(
            factory.Services,
            "follow-up-repo",
            "Docs/Page.cs",
            "// 用户列表页的分页逻辑在 QueryObject 中实现\npublic sealed class QueryObject { }\n");
        var sourceId = await IntegrationTestHelpers.CreateSourceAndPolicyAsync(factory.Services, repositoryId, AcceptMode.Auto, "u1");

        var first = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "第一个问题：分页逻辑在哪里？", "wecom-first");
        await IntegrationTestHelpers.WaitForStreamAsync(client, sourceId, first.Stream!.Id, TimeSpan.FromSeconds(25));

        // 同一个线程的第二个问题会并入同一张工单，但企微的 stream 只能承载一条回复：
        // 回答必须写回第二个问题自己的 stream，否则提问者永远停在“已收到，正在分析代码仓库…”。
        var second = await IntegrationTestHelpers.SendMessageAsync(client, sourceId, "u1", "第二个问题：分页逻辑在哪里？", "wecom-second");
        Assert.NotEqual(first.Stream.Id, second.Stream!.Id);

        var answer = await IntegrationTestHelpers.WaitForStreamAsync(client, sourceId, second.Stream.Id, TimeSpan.FromSeconds(25));

        Assert.True(answer.Finished);
        Assert.Contains("分页", answer.Content);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.Null(ticket.DeliveryError);

        // 一张工单、两次 assignment（第二个问题一次），每次一 Report：多跑一次 Executor 会重复开 PR。
        Assert.Equal(2, await db.Assignments.AsNoTracking().CountAsync(item => item.TicketId == ticket.Id));
        Assert.Equal(2, await db.Reports.AsNoTracking().CountAsync(item => item.TicketId == ticket.Id));

        // 第二个回答落在第二个问题自己的 Channel 上，且状态是“等待拉取”而不是“已送达”。
        var messages = await db.ChannelMessages.AsNoTracking()
            .Where(item => item.Purpose == MessagePurpose.Answer)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Equal(2, messages.Select(item => item.ChannelId).Distinct().Count());
        Assert.All(messages, item => Assert.Equal(MessageStatus.AwaitingPull, item.Status));
    }
}
