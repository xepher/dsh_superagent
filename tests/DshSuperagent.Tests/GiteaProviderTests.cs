using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Providers.Gitea;

namespace DshSuperagent.Tests;

public sealed class GiteaProviderTests
{
    [Fact]
    public async Task Creates_One_Commit_With_Add_Update_Delete_And_Wip_Pull()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "pulls?state=all&limit=50&page=1", "[]");
        handler.Expect("GET", "labels?limit=50&page=1", """[{"id":9,"name":"auto-fix"}]""");
        handler.Expect("GET", "branches/superagent/fix-demo", "{}", HttpStatusCode.NotFound);
        handler.Expect("GET", "contents/src/Program.cs?ref=release%2Fmain", """{"sha":"old-program"}""");
        handler.Expect("GET", "contents/old.txt?ref=release%2Fmain", """{"sha":"old-delete"}""");
        handler.Expect("POST", "contents", "{}", inspect: body =>
        {
            Assert.Equal("release/main", body["branch"]!.GetValue<string>());
            Assert.Equal("superagent/fix-demo", body["new_branch"]!.GetValue<string>());
            Assert.Equal("fix: demo", body["message"]!.GetValue<string>());
            Assert.Equal("Bot", body["author"]!["name"]!.GetValue<string>());
            var files = body["files"]!.AsArray();
            Assert.Equal(3, files.Count);
            Assert.Equal("update", files[0]!["operation"]!.GetValue<string>());
            Assert.Equal("old-program", files[0]!["sha"]!.GetValue<string>());
            Assert.Equal("新内容\n", Encoding.UTF8.GetString(Convert.FromBase64String(files[0]!["content"]!.GetValue<string>())));
            Assert.Equal("create", files[1]!["operation"]!.GetValue<string>());
            Assert.Null(files[1]!["sha"]);
            Assert.Equal("delete", files[2]!["operation"]!.GetValue<string>());
            Assert.Equal("old-delete", files[2]!["sha"]!.GetValue<string>());
            Assert.Null(files[2]!["content"]);
        });
        handler.Expect("POST", "pulls", Pull, inspect: body =>
        {
            Assert.Equal("WIP: fix: demo", body["title"]!.GetValue<string>());
            Assert.Equal("superagent/fix-demo", body["head"]!.GetValue<string>());
            Assert.Equal("release/main", body["base"]!.GetValue<string>());
            Assert.Equal(9, body["labels"]![0]!.GetValue<int>());
            Assert.Null(body["draft"]);
        });

        var result = await Provider(handler).CreateAsync(Profile(), Request() with
        {
            Draft = true,
            Labels = ["auto-fix"],
            Files = [new("src/Program.cs", FileChangeKind.Modified, "新内容\n"),
                new("new.txt", FileChangeKind.Added, "new"), new("old.txt", FileChangeKind.Deleted, null)],
        }, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("7", result.Number);
        Assert.Equal("https://git.example/acme/demo/pulls/7", result.Url);
        handler.AssertComplete();
    }

    [Fact]
    public async Task Reuses_Pr_On_Later_Page_But_Not_Other_Base_Or_Fork()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "pulls?state=all&limit=50&page=1", """
            [{"head":{"ref":"superagent/fix-demo","repo":{"full_name":"other/demo"}},"base":{"ref":"release/main"}},
             {"head":{"ref":"superagent/fix-demo","repo":{"full_name":"acme/demo"}},"base":{"ref":"other"}}]
            """);
        handler.Expect("GET", "pulls?state=all&limit=50&page=2", $"[{Pull}]");

        var result = await Provider(handler).CreateAsync(Profile(), Request(), CancellationToken.None);

        Assert.True(result.Created);
        Assert.Contains("复用", result.Message);
        handler.AssertComplete();
    }

    [Fact]
    public async Task Existing_Branch_Is_Not_Overwritten_When_Retrying_Failed_Pr_Creation()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "pulls?state=all&limit=50&page=1", "[]");
        handler.Expect("GET", "branches/superagent/fix-demo", """{"name":"superagent/fix-demo"}""");
        handler.Expect("POST", "pulls", Pull, inspect: body => Assert.Equal("fix: demo", body["title"]!.GetValue<string>()));

        Assert.True((await Provider(handler).CreateAsync(Profile(), Request(), CancellationToken.None)).Created);
        handler.AssertComplete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("invalid")]
    public async Task DryRun_Defaults_To_Safe_And_Makes_No_Http_Calls(string? dryRun)
    {
        var handler = new GiteaHandler();
        var profile = new PullRequestProfile { ProviderId = "gitea" };
        if (dryRun is not null)
        {
            profile.Configuration["dryRun"] = dryRun;
        }

        Assert.False((await Provider(handler).CreateAsync(profile, Request(), CancellationToken.None)).Created);
        Assert.True((await Provider(handler).CheckAsync(profile, CancellationToken.None)).Ok);
        handler.AssertComplete();
    }

    [Fact]
    public async Task Resolves_Label_Names_Across_Pages_Before_Writing()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "pulls?state=all&limit=50&page=1", "[]");
        handler.Expect("GET", "labels?limit=50&page=1", """[{"id":1,"name":"other"}]""");
        handler.Expect("GET", "labels?limit=50&page=2", """[{"id":12,"name":"auto-fix"}]""");
        handler.Expect("GET", "branches/superagent/fix-demo", "{}");
        handler.Expect("POST", "pulls", Pull, inspect: body => Assert.Equal(12, body["labels"]![0]!.GetValue<int>()));

        await Provider(handler).CreateAsync(Profile(), Request() with { Labels = ["AUTO-FIX", "auto-fix"] }, CancellationToken.None);
        handler.AssertComplete();
    }

    [Fact]
    public async Task Missing_Label_Fails_Before_Any_Write()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "pulls?state=all&limit=50&page=1", "[]");
        handler.Expect("GET", "labels?limit=50&page=1", "[]");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(
            Profile(), Request() with { Labels = ["missing"] }, CancellationToken.None));

        Assert.Contains("标签不存在", error.Message);
        handler.AssertComplete();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolute")]
    [InlineData("src/./file")]
    [InlineData("src\\file")]
    [InlineData(".git/config")]
    public async Task Rejects_Unsafe_Paths_Without_Http(string path)
    {
        var handler = new GiteaHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(Profile(),
            Request() with { Files = [new(path, FileChangeKind.Added, "x")] }, CancellationToken.None));
        handler.AssertComplete();
    }

    [Fact]
    public async Task Rejects_Base_Branch_As_Target_And_Missing_Content()
    {
        var handler = new GiteaHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(Profile(),
            Request() with { BranchName = "release/main" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(Profile(),
            Request() with { Files = [new("x", FileChangeKind.Modified, null)] }, CancellationToken.None));
        handler.AssertComplete();
    }

    [Theory]
    [InlineData("http://git.example/api/v1")]
    [InlineData("https://user:password@git.example/api/v1")]
    [InlineData("https://git.example/api/v1?token=x")]
    [InlineData("")]
    public async Task Rejects_Unsafe_Api_Urls(string url)
    {
        var handler = new GiteaHandler();
        var profile = Profile();
        profile.Configuration["apiBaseUrl"] = url;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(profile, Request(), CancellationToken.None));
        handler.AssertComplete();
    }

    [Fact]
    public async Task Checks_Authenticated_User_And_Does_Not_Echo_Remote_Error_Bodies()
    {
        var handler = new GiteaHandler();
        handler.Expect("GET", "user", """{"login":"bot"}""");
        handler.Expect("GET", "user", """{"message":"secret-token"}""", HttpStatusCode.Unauthorized);
        var provider = Provider(handler);

        var success = await provider.CheckAsync(Profile(), CancellationToken.None);
        var failure = await provider.CheckAsync(Profile(), CancellationToken.None);

        Assert.True(success.Ok);
        Assert.Contains("bot", success.Message);
        Assert.False(failure.Ok);
        Assert.DoesNotContain("secret-token", failure.ToString());
        Assert.Contains("401", failure.ToString());
        handler.AssertComplete();
    }

    [Fact]
    public async Task Missing_Token_Fails_Without_Http()
    {
        var handler = new GiteaHandler();
        var profile = Profile();
        profile.Configuration.Remove("token");
        Assert.False((await Provider(handler).CheckAsync(profile, CancellationToken.None)).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).CreateAsync(profile, Request(), CancellationToken.None));
        handler.AssertComplete();
    }

    private const string Pull = """
        {"html_url":"https://git.example/acme/demo/pulls/7","number":7,
         "head":{"ref":"superagent/fix-demo","repo":{"full_name":"acme/demo"}},"base":{"ref":"release/main"}}
        """;

    private static PullRequestProfile Profile() => new()
    {
        ProviderId = "gitea",
        Configuration = new Dictionary<string, string>
        {
            ["token"] = "test-token", ["apiBaseUrl"] = "https://git.example/gitea/api/v1",
            ["dryRun"] = "false", ["authorName"] = "Bot", ["authorEmail"] = "bot@example.com",
        },
    };

    private static PullRequestRequest Request() => new("acme", "demo", "release/main", "superagent/fix-demo",
        "fix: demo", "body", "fix: demo", [new("new.txt", FileChangeKind.Added, "new")], false, []);

    private static GiteaPullRequestProvider Provider(GiteaHandler handler) => new(new FakeFactory(handler));

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("gitea", name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class GiteaHandler : HttpMessageHandler
    {
        private readonly Queue<(string Method, string Path, string Response, HttpStatusCode Status, Action<JsonNode>? Inspect)> _expected = new();

        public void Expect(string method, string path, string response, HttpStatusCode status = HttpStatusCode.OK,
            Action<JsonNode>? inspect = null) => _expected.Enqueue((method, path, response, status, inspect));

        public void AssertComplete() => Assert.Empty(_expected);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_expected);
            var expected = _expected.Dequeue();
            Assert.Equal(expected.Method, request.Method.Method);
            var path = expected.Path == "user" ? "user" : $"repos/acme/demo/{expected.Path}";
            Assert.Equal($"https://git.example/gitea/api/v1/{path}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("token", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            if (expected.Inspect is not null)
            {
                expected.Inspect(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            }

            return new HttpResponseMessage(expected.Status)
            {
                Content = new StringContent(expected.Response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
