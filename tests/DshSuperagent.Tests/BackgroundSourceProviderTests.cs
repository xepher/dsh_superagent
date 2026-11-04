using System.Net;
using System.Text;
using System.Text.Json;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.Common;
using DshSuperagent.Providers.Feishu;
using DshSuperagent.Providers.GitHub;
using DshSuperagent.Providers.Gitea;
using DshSuperagent.Web.Services;

namespace DshSuperagent.Tests;

public sealed class BackgroundSourceProviderTests
{
    private static JsonElement Json(object value) => SourceJson.Parse(JsonSerializer.Serialize(value));
    private static Source GitHub() => Source.Create("github", "GitHub", new Dictionary<string, string>
    {
        ["cli"] = "gh", ["account"] = "agent", ["repository"] = "team/demo",
    });
    private static Source Gitea() => Source.Create("gitea", "Gitea", new Dictionary<string, string>
    {
        ["endpoint"] = "https://gitea.example.test/api/v1", ["token"] = "fake-test-token",
        ["repository"] = "team/demo", ["observeComments"] = "true",
    });

    [Fact]
    public void Feishu_live_and_history_share_identity_and_empty_roots_do_not_merge_messages()
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var live = FeishuSourceProvider.Normalize(Json(new { message_id = "om_one", chat_id = "oc_demo", chat_type = "p2p",
            sender_id = "ou_alice", sender_type = "user", content = "hello", create_time = at, root_id = "" }), "ou_bot", "cli_app");
        var history = FeishuSourceProvider.Normalize(Json(new { message_id = "om_one", chat_id = "oc_demo",
            sender = new { id = "ou_alice", sender_type = "user" }, body = new { content = "{\"text\":\"hello\"}" },
            create_time = at }), "ou_bot", "cli_app", true);
        Assert.NotNull(live);
        Assert.NotNull(history);
        Assert.Equal(live.ExternalId, history.ExternalId);
        Assert.Equal("oc_demo:om_one", live.ThreadKey);
        Assert.Equal(live.ThreadKey, history.ThreadKey);
        Assert.Equal("hello", history.Text);
        Assert.True(history.Addressed);
        Assert.True(history.Actor.IsHuman);
    }

    [Fact]
    public void Feishu_topic_history_without_root_uses_known_root_and_mentions_are_not_plain_text_matches()
    {
        var message = Json(new { message_id = "om_reply", chat_id = "oc_demo", sender_id = "ou_alice", sender_type = "user",
            content = "@_user_1 fix", mentions = new[] { new { id = "ou_bot", key = "@_user_1", name = "agent" } },
            create_time = "1760000000000" });
        var result = FeishuSourceProvider.Normalize(message, "ou_bot", "cli_app", false, "om_root");
        Assert.NotNull(result);
        Assert.Equal("oc_demo:om_root", result.ThreadKey);
        Assert.Equal("fix", result.Text);
        Assert.True(result.Addressed);
        Assert.Null(FeishuSourceProvider.Normalize(message, "ou_alice", "cli_app"));
        Assert.Throws<FormatException>(() => FeishuSourceProvider.Normalize(Json(new { message_id = "bad" }), "ou_bot", "cli_app"));
    }

    [Fact]
    public async Task Feishu_reply_uses_stable_delivery_id_and_deleted_destination_is_permanent()
    {
        var runner = new Commands(_ => "{}");
        var provider = new FeishuSourceProvider(runner);
        var source = Source.Create("feishu", "Feishu", new Dictionary<string, string> { ["cli"] = "lark-cli" });
        var address = ChannelAddress.Create("feishu", ("messageId", "om_root"), ("deliveryId", "stable-id"));
        await provider.SendAsync(source, address, "hello", default);
        Assert.Contains("--reply-in-thread", runner.Calls.Single().Arguments);
        var args = runner.Calls.Single().Arguments.ToList();
        Assert.Equal("stable-id", args[args.IndexOf("--idempotency-key") + 1]);
        runner.Reply = _ => throw new ProviderCommandException(1, 230011);
        await Assert.ThrowsAsync<ChannelDestinationGoneException>(() => provider.SendAsync(source, address, "hello", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SendAsync(source,
            ChannelAddress.Create("feishu", ("messageId", "om_root")), "hello", default));
    }

    [Fact]
    public async Task GitHub_checks_expected_identity_and_pins_public_host_and_cli_configuration()
    {
        var runner = new Commands(command => Http(command.Arguments[1] == "user"
            ? new { login = "other" } : (object)new { full_name = "team/demo", permissions = new { push = true } }));
        var result = await new GitHubSourceProvider(runner).CheckAsync(GitHub(), default);
        Assert.False(result.Ok);
        Assert.Contains("不匹配", result.Detail);
        var call = runner.Calls.Single();
        Assert.Contains("--hostname", call.Arguments);
        Assert.Contains("github.com", call.Arguments);
        Assert.NotNull(call.Environment);
        Assert.True(call.Environment.ContainsKey("GH_TOKEN"));
        Assert.Null(call.Environment["GH_TOKEN"]);
    }

    [Fact]
    public async Task GitHub_empty_notifications_preserve_server_poll_interval_and_baseline()
    {
        var runner = new Commands(_ => "HTTP/2.0 200 OK\r\nX-Poll-Interval: 180\r\n\r\n[]");
        var runtime = new Runtime();
        var delay = await new GitHubSourceProvider(runner).PollOnceAsync(GitHub(), runtime, "agent", default);
        Assert.Equal(180, delay);
        Assert.Contains("notifications", runtime.State.Keys);
        Assert.Empty(runtime.Events);
    }

    [Theory]
    [InlineData("@agent please", true)]
    [InlineData("mail@agent.example", false)]
    [InlineData("@agent-other", false)]
    [InlineData("@AGENT: please", true)]
    public void Code_host_mentions_match_login_boundaries(string text, bool expected) =>
        Assert.Equal(expected, CodeHostSourceProvider.Mentions(text, "agent"));

    [Fact]
    public async Task Gitea_uses_review_specific_api_and_keeps_review_and_inline_event_namespaces_distinct()
    {
        var paths = new List<string>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var clients = new Clients(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            Assert.Equal("token", request.Headers.Authorization?.Scheme);
            var page2 = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
            object data = Array.Empty<object>();
            if (!page2 && path.EndsWith("/issues"))
                data = new[] { new { number = 1, pull_request = new { }, updated_at = now } };
            else if (!page2 && path.EndsWith("/pulls/1/reviews"))
                data = new[] { new { id = 7, state = "APPROVED", body = "review", user = new { login = "alice" }, submitted_at = now } };
            else if (path.EndsWith("/pulls/1/reviews/7/comments"))
                data = new[] { new { id = 7, body = "inline", user = new { login = "alice" }, updated_at = now, path = "a.cs" } };
            return data;
        });
        var runtime = new Runtime();
        runtime.State["stream:issues"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        runtime.State["enabled:observeComments"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        await new GiteaSourceProvider(clients).PollOnceAsync(Gitea(), runtime, "agent", default);
        Assert.Contains("/api/v1/repos/team/demo/pulls/1/reviews/7/comments", paths);
        Assert.DoesNotContain(paths, path => path.EndsWith("/pulls/comments") || path.EndsWith("/pulls/1/comments"));
        Assert.Equal(2, runtime.Events.Count);
        Assert.Equal(2, runtime.Events.Select(item => item.ExternalId).Distinct().Count());
    }

    [Fact]
    public void Pull_request_addresses_cannot_cross_repository_or_host()
    {
        var provider = new GiteaSourceProvider(new Clients(_ => new { }));
        Assert.NotNull(provider.AddressForPullRequest(Gitea(), "https://gitea.example.test/team/demo/pulls/12"));
        Assert.Null(provider.AddressForPullRequest(Gitea(), "https://other.example.test/team/demo/pulls/12"));
        Assert.Null(provider.AddressForPullRequest(Gitea(), "https://gitea.example.test/team/other/pulls/12"));
        Assert.Null(provider.AddressForPullRequest(Gitea(), "https://gitea.example.test/team/demo/pulls/12/extra"));
    }

    [Fact]
    public void Legacy_read_positions_and_json_state_remain_compatible()
    {
        Assert.Empty(SourceRuntime.ParseState("2026-01-01T00:00:00Z"));
        Assert.Equal("value", SourceRuntime.ParseState("{\"key\":\"value\"}")["key"]);
    }

    [Fact]
    public async Task GitHub_inline_reply_uses_documented_endpoint_and_retry_marker_prevents_second_post()
    {
        var replies = new List<object>();
        var posts = 0;
        var runner = new Commands(command =>
        {
            var path = command.Arguments[1];
            if (path == "user") return Http(new { login = "agent" });
            if (path == "repos/team/demo/pulls/comments/7") return Http(new { id = 7 });
            if (command.Input is not null)
            {
                Assert.Equal("repos/team/demo/pulls/1/comments/7/replies", path);
                var body = SourceJson.Parse(command.Input).String("body");
                Assert.Contains("<!-- dsh-superagent:stable -->", body);
                replies.Add(new { body, user = new { login = "agent" }, in_reply_to_id = 7 });
                posts++;
                return Http(new { id = 8 });
            }
            return Http(path.Contains("page=2", StringComparison.Ordinal) ? Array.Empty<object>() : replies.ToArray());
        });
        var provider = new GitHubSourceProvider(runner);
        var address = ChannelAddress.Create("github", ("repository", "team/demo"), ("number", "1"),
            ("reviewRootId", "7"), ("deliveryId", "stable"));
        await provider.SendAsync(GitHub(), address, "reply", default);
        await provider.SendAsync(GitHub(), address, "reply", default);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Failed_comment_stream_preserves_cursor_and_later_success_advances_it()
    {
        var source = GitHub();
        var config = new Dictionary<string, string>(source.Configuration) { ["observeComments"] = "true" };
        source.Configure(source.Name, config, source.DefaultRepositoryId);
        var runtime = new Runtime();
        var previous = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        runtime.State["stream:comments"] = previous;
        var runner = new Commands(command => command.Arguments[1].Contains("/issues/comments?", StringComparison.Ordinal)
            ? throw new ProviderCommandException(1, 503) : Http(Array.Empty<object>()));
        var provider = new GitHubSourceProvider(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PollOnceAsync(source, runtime, "agent", default));
        Assert.Equal(previous, runtime.State["stream:comments"]);
        runner.Reply = _ => Http(Array.Empty<object>());
        await provider.PollOnceAsync(source, runtime, "agent", default);
        Assert.NotEqual(previous, runtime.State["stream:comments"]);
    }

    private static string Http(object body) => "HTTP/2.0 200 OK\r\n\r\n" + JsonSerializer.Serialize(body);
    private sealed class Commands(Func<ProviderCommand, string> reply) : IProviderCommandRunner
    {
        public Func<ProviderCommand, string> Reply { get; set; } = reply;
        public List<ProviderCommand> Calls { get; } = [];
        public Task<string> RunAsync(ProviderCommand command, CancellationToken cancellationToken)
        {
            Calls.Add(command);
            return Task.FromResult(Reply(command));
        }
        public Task WatchAsync(ProviderCommand command, Func<string, CancellationToken, Task> receive,
            Func<string, CancellationToken, Task> diagnostic, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Clients(Func<HttpRequestMessage, object> reply) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(reply));
        private sealed class Handler(Func<HttpRequestMessage, object> reply) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(reply(request)), Encoding.UTF8, "application/json"),
                });
        }
    }
    private sealed class Runtime : ISourceRuntime
    {
        public Dictionary<string, string> State { get; } = [];
        public List<InboundEvent> Events { get; } = [];
        public Task ReceiveAsync(InboundEvent inbound, CancellationToken token) { Events.Add(inbound); return Task.CompletedTask; }
        public Task<string?> ReadStateAsync(string key, CancellationToken token) => Task.FromResult(State.GetValueOrDefault(key));
        public Task WriteStateAsync(string key, string? value, CancellationToken token)
        {
            if (value is null) State.Remove(key); else State[key] = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, string>> ReadStatePrefixAsync(string prefix, CancellationToken token) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(State.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary());
        public Task<IReadOnlyList<SourceThread>> GetSubscribedThreadsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SourceThread>>([]);
        public Task ObservePullRequestAsync(string key, bool merged, bool closed, string url, CancellationToken token) => Task.CompletedTask;
        public Task ReportAsync(string component, string status, string detail, CancellationToken token) => Task.CompletedTask;
    }
}
