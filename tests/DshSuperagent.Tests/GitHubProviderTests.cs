using System.Net;
using System.Text;
using System.Text.Json;
using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Providers.GitHub;

namespace DshSuperagent.Tests;

public sealed class GitHubProviderTests
{
    [Fact]
    public async Task Creates_Branch_Commit_And_PullRequest_Through_Git_Data_Api()
    {
        var handler = new GitHubHandler();
        var provider = new GitHubPullRequestProvider(new FakeFactory(handler));
        var profile = new PullRequestProfile
        {
            ProviderId = "github",
            Configuration = new Dictionary<string, string>
            {
                ["token"] = "test-token",
                ["apiBaseUrl"] = "https://api.github.com",
                ["dryRun"] = "false",
                ["draft"] = "true",
                ["labels"] = "auto-fix,code-quality",
            },
        };
        var request = new PullRequestRequest(
            "acme",
            "demo",
            "main",
            "superagent/fix-demo",
            "fix: demo",
            "body",
            "fix: demo",
            [new PullRequestFile("Program.cs", FileChangeKind.Modified, "throw new InvalidOperationException();\n")],
            Draft: true,
            Labels: ["auto-fix"]);

        var result = await provider.CreateAsync(profile, request, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("https://github.com/acme/demo/pull/7", result.Url);
        Assert.Equal("7", result.Number);
        Assert.Equal(
            [
                "GET ref", "GET commit", "POST blob", "POST tree", "POST commit", "GET branch ref", "POST ref", "GET pulls", "POST pull", "POST labels",
            ],
            handler.Steps);
        Assert.Equal("throw new InvalidOperationException();\n", handler.BlobContent);
        Assert.Contains("\"head\":\"superagent/fix-demo\"", handler.PullBody);
        Assert.True(handler.PullDraft);
        Assert.Contains("\"labels\":[\"auto-fix\"]", handler.LabelBody);
    }

    [Fact]
    public async Task Existing_Branch_And_Pr_Are_Reused_Without_Creating_Ref()
    {
        var handler = new GitHubHandler(branchExists: true, pullExists: true);
        var provider = new GitHubPullRequestProvider(new FakeFactory(handler));
        var profile = new PullRequestProfile
        {
            ProviderId = "github",
            Configuration = new Dictionary<string, string>
            {
                ["token"] = "test-token",
                ["apiBaseUrl"] = "https://api.github.com",
                ["dryRun"] = "false",
            },
        };
        var request = new PullRequestRequest(
            "acme",
            "demo",
            "main",
            "superagent/fix-demo",
            "fix: demo",
            "body",
            "fix: demo",
            [new PullRequestFile("Program.cs", FileChangeKind.Modified, "x")],
            Draft: false,
            Labels: []);

        var result = await provider.CreateAsync(profile, request, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("https://github.com/acme/demo/pull/7", result.Url);
        Assert.DoesNotContain("POST ref", handler.Steps);
        Assert.DoesNotContain("POST pull", handler.Steps);
        Assert.Contains("GET pulls", handler.Steps);
    }

    [Fact]
    public async Task DryRun_Makes_No_Http_Calls()
    {
        var handler = new GitHubHandler();
        var provider = new GitHubPullRequestProvider(new FakeFactory(handler));
        var profile = new PullRequestProfile
        {
            ProviderId = "github",
            Configuration = new Dictionary<string, string>
            {
                ["token"] = "test-token",
                ["apiBaseUrl"] = "https://api.github.com",
                ["dryRun"] = "true",
            },
        };
        var request = new PullRequestRequest(
            "acme",
            "demo",
            "main",
            "branch",
            "title",
            "body",
            "commit",
            [new PullRequestFile("Program.cs", FileChangeKind.Modified, "x")],
            Draft: false,
            Labels: []);

        var result = await provider.CreateAsync(profile, request, CancellationToken.None);

        Assert.False(result.Created);
        Assert.Empty(handler.Steps);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class GitHubHandler(bool branchExists = false, bool pullExists = false) : HttpMessageHandler
    {
        public List<string> Steps { get; } = [];
        public string? BlobContent { get; private set; }
        public string PullBody { get; private set; } = string.Empty;
        public bool PullDraft { get; private set; }
        public string LabelBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string response;
            if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
            {
                Steps.Add("GET ref");
                response = """{"object":{"sha":"base-commit"}}""";
            }
            else if (request.Method == HttpMethod.Get && path.Contains("/git/ref/heads/superagent/", StringComparison.Ordinal))
            {
                Steps.Add("GET branch ref");
                if (!branchExists)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("""{"message":"Not Found"}""", Encoding.UTF8, "application/json"),
                    };
                }

                response = """{"object":{"sha":"branch-commit"}}""";
            }
            else if (request.Method == HttpMethod.Get && path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                Steps.Add("GET pulls");
                response = pullExists
                    ? """[{"html_url":"https://github.com/acme/demo/pull/7","number":7}]"""
                    : "[]";
            }
            else if (request.Method == HttpMethod.Get && path.Contains("/git/commits/"))
            {
                Steps.Add("GET commit");
                response = """{"tree":{"sha":"base-tree"}}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
            {
                Steps.Add("POST blob");
                using var document = JsonDocument.Parse(body);
                var content = document.RootElement.GetProperty("content").GetString()!;
                BlobContent = Encoding.UTF8.GetString(Convert.FromBase64String(content));
                response = """{"sha":"blob-1"}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
            {
                Steps.Add("POST tree");
                response = """{"sha":"new-tree"}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
            {
                Steps.Add("POST commit");
                response = """{"sha":"new-commit"}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/git/refs", StringComparison.Ordinal))
            {
                Steps.Add("POST ref");
                response = """{"ref":"refs/heads/superagent/fix-demo"}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                Steps.Add("POST pull");
                PullBody = body;
                using var document = JsonDocument.Parse(body);
                PullDraft = document.RootElement.GetProperty("draft").GetBoolean();
                response = """{"html_url":"https://github.com/acme/demo/pull/7","number":7}""";
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/labels", StringComparison.Ordinal))
            {
                Steps.Add("POST labels");
                LabelBody = body;
                response = "[]";
            }
            else
            {
                throw new InvalidOperationException($"unexpected {request.Method} {path}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
