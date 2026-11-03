using System.Net;
using System.Text;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.WeCom;

namespace DshSuperagent.Tests;

public sealed class WeComProviderTests
{
    [Fact]
    public async Task ResponseUrl_Reply_Posts_Markdown_To_Whitelisted_Host()
    {
        var handler = new RecordingHandler();
        var provider = new WeComAibotSourceProvider(new FakeHttpClientFactory(handler), new FakeStreamStore());
        var source = Source.Create(
            WeComAibotSourceProvider.ProviderId,
            "wecom",
            new Dictionary<string, string>
            {
                ["token"] = "token",
                ["encodingAesKey"] = Convert.ToBase64String(new byte[32]).TrimEnd('='),
                ["replyMode"] = "response_url",
            });
        var address = ChannelAddress.Create(
            WeComAibotSourceProvider.ProviderId,
            ("response_url", "https://qyapi.weixin.qq.com/cgi-bin/aibot/response?response_code=abc"));

        await provider.SendAsync(source, address, "**answer**", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("https://qyapi.weixin.qq.com/cgi-bin/aibot/response?response_code=abc", handler.Requests[0].Uri.ToString());
        Assert.Contains("\"markdown\"", handler.Requests[0].Body);
        Assert.Contains("**answer**", handler.Requests[0].Body);
    }

    [Fact]
    public async Task ResponseUrl_Outside_Whitelist_Is_Rejected()
    {
        var handler = new RecordingHandler();
        var provider = new WeComAibotSourceProvider(new FakeHttpClientFactory(handler), new FakeStreamStore());
        var source = Source.Create(
            WeComAibotSourceProvider.ProviderId,
            "wecom",
            new Dictionary<string, string>
            {
                ["token"] = "token",
                ["encodingAesKey"] = Convert.ToBase64String(new byte[32]).TrimEnd('='),
            });
        var address = ChannelAddress.Create(
            WeComAibotSourceProvider.ProviderId,
            ("response_url", "https://evil.example/cgi-bin/aibot/response?response_code=abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(source, address, "answer", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeStreamStore : IStreamReplyStore
    {
        public Task<StreamReplyState?> GetAsync(string streamId, CancellationToken cancellationToken) =>
            Task.FromResult<StreamReplyState?>(null);

        public Task SetRunningAsync(string streamId, string content, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(string streamId, string content, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    public sealed record RecordedRequest(Uri Uri, string Body);
}
