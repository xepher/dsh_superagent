using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Providers.Common;

public sealed record CodeHostResponse(JsonElement Data, IReadOnlyDictionary<string, string> Headers);
public sealed class CodeHostException(int status, bool locked = false) : InvalidOperationException($"代码托管 API 返回 HTTP {status}。")
{
    public int Status { get; } = status;
    public bool Locked { get; } = locked;
}

/// <summary>Shared Issue/PR intake semantics. Concrete adapters own credentials, transport and API differences.</summary>
public abstract class CodeHostSourceProvider : IPullRequestSourceProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ProviderConfigField> ConfigFields { get; }
    public SourceCapabilities Capabilities { get; } = new(ProactiveReply: true);
    protected abstract string WebBase(Source source);
    protected abstract bool Github { get; }
    protected abstract Task<CodeHostResponse> ApiAsync(Source source, string method, string path, object? body, CancellationToken token);

    protected static IReadOnlyList<ProviderConfigField> PollFields { get; } =
    [
        new("repository", "监听仓库 owner/name", ProviderFieldType.Text, Required: true),
        new("pollSeconds", "轮询间隔（秒）", ProviderFieldType.Number, DefaultValue: "60", Help: "60–3600 秒；服务端要求更长间隔时以服务端为准。"),
        new("failuresBeforeError", "连续失败告警阈值", ProviderFieldType.Number, DefaultValue: "3"),
        new("observeIssues", "接收未 @账号的新 Issue", ProviderFieldType.Bool, DefaultValue: "false"),
        new("observePulls", "接收未 @账号的新 PR", ProviderFieldType.Bool, DefaultValue: "false"),
        new("observeComments", "接收仓库评论和代码评论", ProviderFieldType.Bool, DefaultValue: "false"),
        new("observeCi", "接收默认分支 CI 变化", ProviderFieldType.Bool, DefaultValue: "false"),
    ];

    protected static string Repository(Source source)
    {
        var repository = source.Required("repository");
        if (!Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
            || repository.Split('/').Any(part => part is "." or ".."))
            throw new InvalidOperationException("代码托管来源仓库必须是 owner/name。");
        return repository;
    }
    private static string RepoPath(Source source, string suffix = "") => "repos/" + Repository(source) + (suffix.Length == 0 ? "" : "/" + suffix);

    public Task<SourceCallbackResult> HandleCallbackAsync(Source source, ProviderCallbackRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceCallbackResult { StatusCode = 405, Body = "该来源使用原版样式轮询，不接受 HTTP 注入。" });

    public async Task<ProviderCheckResult> CheckAsync(Source source, CancellationToken cancellationToken)
    {
        try
        {
            var login = await ResolveLoginAsync(source, cancellationToken);
            return new(true, $"{DisplayName} 来源账号及仓库写权限检查通过。", $"{login} · {Repository(source)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, "来源配置检查失败。", ex.Message); }
    }

    private async Task<string> ResolveLoginAsync(Source source, CancellationToken token)
    {
        var user = (await ApiAsync(source, "GET", "user", null, token)).Data;
        var repo = (await ApiAsync(source, "GET", RepoPath(source), null, token)).Data;
        if (!string.Equals(repo.String("full_name"), Repository(source), StringComparison.OrdinalIgnoreCase)
            || !repo.Child("permissions").Flag("push"))
            throw new InvalidOperationException("来源账号必须对配置仓库有写权限。");
        return user.String("login") ?? throw new InvalidOperationException("无法读取代码托管账号。");
    }

    public async Task RunAsync(Source source, ISourceRuntime runtime, CancellationToken cancellationToken)
    {
        var login = await ResolveLoginAsync(source, cancellationToken);
        var identity = WebBase(source).TrimEnd('/') + "/" + Repository(source) + ":" + login;
        var previous = await runtime.ReadStateAsync("identity", cancellationToken);
        if (previous is not null && !string.Equals(previous, identity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("来源仓库、服务端或账号身份已改变，请新建 Source，不能混用旧线程历史。");
        await runtime.WriteStateAsync("identity", identity, cancellationToken);
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = source.Number("pollSeconds", 60, 60, 3600);
            try
            {
                delay = Math.Max(delay, await PollOnceAsync(source, runtime, login, cancellationToken));
                failures = 0;
                await runtime.ReportAsync("poll", "ready", $"通知、订阅讨论及 PR 状态已同步；下一轮不少于 {delay} 秒。", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                await runtime.ReportAsync("poll", failures >= source.Number("failuresBeforeError", 3, 1, 20) ? "error" : "retrying",
                    ex.Message, cancellationToken);
                delay = Math.Max(delay, (int)Math.Min(3600, 60 * Math.Pow(2, Math.Min(failures, 6))));
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
    }

    public async Task<int> PollOnceAsync(Source source, ISourceRuntime runtime, string login, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var failures = new List<Exception>();
        var read = new HashSet<int>();
        var interval = 60;
        var beginnings = new Dictionary<string, string>();
        foreach (var flag in new[] { "observeIssues", "observePulls", "observeComments", "observeCi" })
        {
            if (source.Option(flag)) beginnings[flag] = await BaselineAsync(runtime, "enabled:" + flag, started, token);
            else await runtime.WriteStateAsync("enabled:" + flag, null, token);
        }
        async Task Attempt(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex); }
        }
        await Attempt(async () =>
        {
            const string cursor = "notifications";
            var since = await BaselineAsync(runtime, cursor, started, token);
            var failed = 0;
            await foreach (var response in PagesAsync(source, RepoPath(source, "notifications") + "?all=true&since=" + Escape(since), token))
            {
                if (response.Headers.TryGetValue("X-Poll-Interval", out var asked) && int.TryParse(asked, out var seconds))
                    interval = Math.Max(interval, seconds);
                foreach (var item in response.Data.EnumerateArray())
                {
                    if (!string.Equals(item.Child("repository").String("full_name"), Repository(source), StringComparison.OrdinalIgnoreCase)) continue;
                    var subject = item.Child("subject");
                    var address = NotificationAddress(source, subject.String("url"));
                    if (address is null || !int.TryParse(address.Get("number"), out var number) || !read.Add(number)) continue;
                    var retryKey = "notification-retry:" + number;
                    try
                    {
                        await PollThreadAsync(source, runtime, login, number, since, started, token);
                        if (await runtime.ReadStateAsync(retryKey, token) is not null)
                        {
                            await runtime.WriteStateAsync(retryKey, null, token);
                            await runtime.WriteStateAsync("notification-abandoned:" + number, null, token);
                            await runtime.WriteStateAsync("health:notification:" + number, null, token);
                        }
                    }
                    catch (CodeHostException ex) when (ex.Status == 410) { /* Permanently deleted notification target. */ }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var first = await runtime.ReadStateAsync(retryKey, token);
                        if (first is null) await runtime.WriteStateAsync(retryKey, started.ToString("O"), token);
                        if (first is not null && SourceJson.Time(first) <= started.AddHours(-24))
                        {
                            var abandoned = "notification-abandoned:" + number;
                            if (await runtime.ReadStateAsync(abandoned, token) != first)
                            {
                                await runtime.ReportAsync("notification:" + number, "error", "目标连续 24 小时无法读取，放弃本次通知；其他通知继续接收。", token);
                                await runtime.WriteStateAsync(abandoned, first, token);
                            }
                        }
                        else failed++;
                    }
                }
            }
            if (failed > 0) throw new InvalidOperationException($"{failed} 条通知读取失败，通知游标保留并重试；其他流继续接收。");
            await runtime.WriteStateAsync(cursor, Overlap(started), token);
        });
        foreach (var (key, enabled, path) in new[]
        {
            ("issues", source.Option("observeIssues") || source.Option("observePulls") || (!Github && source.Option("observeComments")), "issues?state=all"),
            ("comments", source.Option("observeComments"), "issues/comments?"),
            ("inline", Github && source.Option("observeComments"), "pulls/comments?"),
        })
        {
            await Attempt(async () =>
            {
                var cursor = "stream:" + key;
                if (!enabled) { await runtime.WriteStateAsync(cursor, null, token); return; }
                var since = await BaselineAsync(runtime, cursor, started, token);
                var separator = path.EndsWith('?') ? "" : "&";
                var sort = Github ? "sort=updated&direction=asc&" : "";
                await foreach (var page in PagesAsync(source, RepoPath(source, path) + separator + sort + "since=" + Escape(since), token))
                {
                    foreach (var item in page.Data.EnumerateArray())
                    {
                        if (key == "issues")
                        {
                            var pr = item.Child("pull_request").ValueKind == JsonValueKind.Object;
                            if (!Github && pr && beginnings.TryGetValue("observeComments", out var commentsBeginning)
                                && Fresh(item.String("updated_at"), since) && int.TryParse(item.String("number"), out var reviewNumber))
                                await PollReviewsAsync(source, runtime, login, reviewNumber,
                                    SourceJson.Time(commentsBeginning) > SourceJson.Time(since) ? commentsBeginning : since, token);
                            var flag = pr ? "observePulls" : "observeIssues";
                            if (!beginnings.TryGetValue(flag, out var beginning)) continue;
                            var from = SourceJson.Time(beginning) > SourceJson.Time(since) ? beginning : since;
                            await ReceiveDescriptionAsync(source, runtime, item, login, pr, from, token);
                        }
                        else
                        {
                            var link = item.String("issue_url") ?? item.String("pull_request_url") ?? item.String("html_url");
                            var address = NotificationAddress(source, link);
                            if (address is null || !int.TryParse(address.Get("number"), out var number)) continue;
                            await ReceiveCommentAsync(source, runtime, item, number, login, key == "inline", since, token);
                        }
                    }
                }
                await runtime.WriteStateAsync(cursor, Overlap(started), token);
            });
        }
        foreach (var thread in await runtime.GetSubscribedThreadsAsync(token))
        {
            if (!int.TryParse(thread.Address.Get("number"), out var number) || !read.Add(number)) continue;
            await Attempt(() => PollThreadAsync(source, runtime, login, number, thread.CreatedAt.AddSeconds(-5).ToString("O"), started, token));
        }
        await Attempt(async () =>
        {
            if (!source.Option("observeCi")) return;
            var repo = (await ApiAsync(source, "GET", RepoPath(source), null, token)).Data;
            var branch = repo.String("default_branch") ?? throw new InvalidOperationException("仓库缺少默认分支。");
            var commit = (await ApiAsync(source, "GET", RepoPath(source, "commits/" + Escape(branch)), null, token)).Data;
            if (commit.String("sha") is { Length: > 0 } sha)
                await PollCiAsync(source, runtime, sha, Id + ":ci:" + branch, null, token, beginnings["observeCi"]);
        });
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} 个来源读取失败：{failures[0].Message}");
        return Math.Max(60, interval);
    }

    private async Task PollThreadAsync(Source source, ISourceRuntime runtime, string login, int number,
        string baseline, DateTimeOffset started, CancellationToken token)
    {
        var cursor = "thread:" + number;
        var since = await runtime.ReadStateAsync(cursor, token) ?? baseline;
        var issue = (await ApiAsync(source, "GET", RepoPath(source, "issues/" + number), null, token)).Data;
        var pr = issue.Child("pull_request").ValueKind == JsonValueKind.Object;
        await ReceiveDescriptionAsync(source, runtime, issue, login, pr, since, token, includeEdits: true);
        if (pr)
        {
            var pull = (await ApiAsync(source, "GET", RepoPath(source, "pulls/" + number), null, token)).Data;
            var url = pull.String("html_url") ?? PullUrl(source, number);
            await runtime.ObservePullRequestAsync(ThreadKey(number), pull.Flag("merged"), pull.String("state") == "closed", url, token);
            if (!pull.Flag("merged") && pull.String("state") != "closed" && pull.Child("head").String("sha") is { Length: > 0 } sha)
                await PollCiAsync(source, runtime, sha, ThreadKey(number), number, token);
        }
        await foreach (var response in PagesAsync(source, RepoPath(source, $"issues/{number}/comments") + "?since=" + Escape(since), token))
            foreach (var item in response.Data.EnumerateArray())
                await ReceiveCommentAsync(source, runtime, item, number, login, false, since, token);
        if (pr) await PollReviewsAsync(source, runtime, login, number, since, token);
        await runtime.WriteStateAsync(cursor, Overlap(started), token);
    }

    private async Task PollReviewsAsync(Source source, ISourceRuntime runtime, string login, int number, string since, CancellationToken token)
    {
        if (Github)
        {
            await foreach (var response in PagesAsync(source, RepoPath(source, $"pulls/{number}/comments") + "?since=" + Escape(since), token))
                foreach (var item in response.Data.EnumerateArray())
                    await ReceiveCommentAsync(source, runtime, item, number, login, true, since, token);
        }
        await foreach (var response in PagesAsync(source, RepoPath(source, $"pulls/{number}/reviews"), token))
        {
            foreach (var review in response.Data.EnumerateArray())
            {
                await ReceiveCommentAsync(source, runtime, review, number, login, false, since, token);
                if (Github || !long.TryParse(review.String("id"), out var reviewId) || reviewId <= 0) continue;
                // Gitea 1.24 exposes unpaginated review comments under each review, not /pulls/comments.
                var comments = (await ApiAsync(source, "GET", RepoPath(source, $"pulls/{number}/reviews/{reviewId}/comments"), null, token)).Data;
                if (comments.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Gitea review 评论响应不是数组。");
                foreach (var comment in comments.EnumerateArray())
                    await ReceiveCommentAsync(source, runtime, comment, number, login, true, since, token);
            }
        }
    }

    private async Task ReceiveDescriptionAsync(Source source, ISourceRuntime runtime, JsonElement item, string login, bool pr,
        string since, CancellationToken token, bool includeEdits = false)
    {
        var author = item.Child("user").String("login") ?? "ghost";
        if (string.Equals(author, login, StringComparison.OrdinalIgnoreCase)) return;
        var time = includeEdits ? item.String("updated_at") ?? item.String("created_at") : item.String("created_at");
        if (!int.TryParse(item.String("number"), out var number) || number <= 0 || !Fresh(time, since)) return;
        var title = item.String("title") ?? $"#{number}";
        var text = title + "\n\n" + (item.String("body") ?? "");
        var url = item.String("html_url") ?? (pr ? PullUrl(source, number) : WebBase(source).TrimEnd('/') + "/" + Repository(source) + "/issues/" + number);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        await runtime.ReceiveAsync(new InboundEvent($"{Id}:{(pr ? "pull" : "issue")}:{number}:{version}", ThreadKey(number), $"#{number} {title}",
            Actor(item.Child("user")), text, Mentions(text, login), false, Repository(source), Address(source, number, url),
            Title: title, PayloadJson: JsonSerializer.Serialize(new { kind = pr ? "pull" : "issue", number, url }),
            ReceivedAt: SourceJson.Time(time)), token);
    }

    private async Task ReceiveCommentAsync(Source source, ISourceRuntime runtime, JsonElement item, int number, string login,
        bool inline, string since, CancellationToken token)
    {
        var author = item.Child("user").String("login") ?? "ghost";
        if (string.Equals(author, login, StringComparison.OrdinalIgnoreCase)) return;
        var time = item.String("updated_at") ?? item.String("submitted_at") ?? item.String("created_at");
        if (!Fresh(time, since) || item.String("id") is not { Length: > 0 } id) return;
        var text = item.String("body") ?? "";
        if (item.String("state") is { Length: > 0 } state) text = $"Review {state}\n{text}";
        if (string.IsNullOrWhiteSpace(text)) return;
        var address = Address(source, number, item.String("html_url"));
        var messageKind = inline ? "inline" : item.String("state") is not null ? "review" : "comment";
        if (inline) address.Data["reviewRootId"] = item.String("in_reply_to_id") ?? id;
        await runtime.ReceiveAsync(new InboundEvent($"{Id}:{messageKind}:{id}:{time}", ThreadKey(number),
            $"{DisplayName} #{number}", Actor(item.Child("user")), text, Mentions(text, login), false, Repository(source), address,
            PayloadJson: JsonSerializer.Serialize(new { number, kind = messageKind, url = item.String("html_url"), path = item.String("path"), line = item.String("line") }),
            ReceivedAt: SourceJson.Time(time)), token);
    }

    private sealed record CiStatus(string Name, string State);

    private async Task PollCiAsync(Source source, ISourceRuntime runtime, string sha, string threadKey, int? number, CancellationToken token, string? enabledSince = null)
    {
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{7,64}$")) throw new InvalidOperationException("CI 提交标识无效。");
        var status = (await ApiAsync(source, "GET", RepoPath(source, "commits/" + sha + "/status"), null, token)).Data;
        var statuses = new List<CiStatus>();
        await foreach (var page in ObjectPagesAsync(source, RepoPath(source, "commits/" + sha + "/status"), "statuses", token))
            statuses.AddRange(page.Select(item => new CiStatus(item.String("context") ?? "status", item.String("state") ?? "unknown")));
        if (Github)
        {
            await foreach (var page in ObjectPagesAsync(source, RepoPath(source, "commits/" + sha + "/check-runs"), "check_runs", token))
                statuses.AddRange(page.Select(item => new CiStatus(item.String("name") ?? "check", item.String("conclusion") ?? item.String("status") ?? "unknown")));
        }
        statuses.Add(new CiStatus("combined", status.String("state") ?? "unknown"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sha, statuses = statuses.OrderBy(item => item.Name).ThenBy(item => item.State).ToArray(),
        }))));
        var key = "ci:" + threadKey;
        var previous = await runtime.ReadStateAsync(key, token);
        var old = previous is null ? default : SourceJson.Parse(previous);
        var established = previous is not null && (enabledSince is null
            || (old.String("observedAt") is { } observed && SourceJson.Time(observed) >= SourceJson.Time(enabledSince)));
        if (established && old.String("fingerprint") == fingerprint) return;
        var revision = int.TryParse(old.String("revision"), out var before) ? before + 1 : 0;
        if (established)
        {
            var summary = string.Join("\n", statuses.Select(item => $"- {item.Name}: {item.State}"));
            var address = number is { } n ? Address(source, n, PullUrl(source, n)) : ChannelAddress.Create("none");
            await runtime.ReceiveAsync(new InboundEvent($"{Id}:ci:{threadKey}:{revision}", threadKey, "CI 状态变化",
                new TriggerActor(Id + ":ci", DisplayName + " CI", ActorKind.System), "CI 状态变化\n" + summary, false, false,
                Repository(source), address, PayloadJson: JsonSerializer.Serialize(new { ci = true, sha, number }), ReceivedAt: DateTimeOffset.UtcNow), token);
        }
        await runtime.WriteStateAsync(key, JsonSerializer.Serialize(new { fingerprint, revision, observedAt = DateTimeOffset.UtcNow }), token);
    }

    private async IAsyncEnumerable<IReadOnlyList<JsonElement>> ObjectPagesAsync(Source source, string path, string property,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        for (var page = 1; page <= 200; page++)
        {
            var result = (await ApiAsync(source, "GET", path + $"?per_page=50&limit=50&page={page}", null, token)).Data.Child(property);
            if (result.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("代码托管列表响应无效。");
            var items = result.EnumerateArray().Select(item => item.Clone()).ToList();
            if (items.Count == 0) yield break;
            yield return items;
            if (items.Count < 50) yield break;
        }
        throw new InvalidOperationException("代码托管分页超过 200 页，读取游标未推进。");
    }

    private async IAsyncEnumerable<CodeHostResponse> PagesAsync(Source source, string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var seen = new HashSet<string>();
        for (var page = 1; page <= 200; page++)
        {
            var response = await ApiAsync(source, "GET", path + (path.Contains('?') ? "&" : "?") + $"per_page=50&limit=50&page={page}", null, token);
            if (response.Data.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("代码托管列表响应不是数组。");
            if (response.Data.GetArrayLength() == 0)
            {
                if (page == 1) yield return response; // Keep X-Poll-Interval even when there are no notifications.
                yield break;
            }
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Data.GetRawText())));
            if (!seen.Add(signature)) throw new InvalidOperationException("代码托管分页未前进，读取游标未推进。");
            yield return response;
        }
        throw new InvalidOperationException("代码托管分页超过 200 页，读取游标未推进。");
    }

    private static async Task<string> BaselineAsync(ISourceRuntime runtime, string key, DateTimeOffset started, CancellationToken token)
    {
        var since = await runtime.ReadStateAsync(key, token);
        if (since is not null) return since;
        since = started.ToString("O");
        await runtime.WriteStateAsync(key, since, token);
        return since;
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Overlap(DateTimeOffset time) => time.AddSeconds(-5).ToString("O");
    private static bool Fresh(string? time, string since) => time is not null && SourceJson.Time(time) >= SourceJson.Time(since);
    public static bool Mentions(string text, string login) => Regex.IsMatch(text, @"(?<![A-Za-z0-9_])@" + Regex.Escape(login) + @"(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static TriggerActor Actor(JsonElement user)
    {
        var login = user.String("login") ?? "ghost";
        return new TriggerActor(login, login, string.Equals(user.String("type"), "Bot", StringComparison.OrdinalIgnoreCase)
            || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? ActorKind.Bot : ActorKind.Human);
    }
    private string ThreadKey(int number) => Id + ":" + number.ToString(CultureInfo.InvariantCulture);
    private string PullUrl(Source source, int number) => WebBase(source).TrimEnd('/') + "/" + Repository(source) + (Github ? "/pull/" : "/pulls/") + number;
    private ChannelAddress Address(Source source, int number, string? url) => ChannelAddress.Create(Id,
        ("number", number.ToString(CultureInfo.InvariantCulture)), ("threadKey", ThreadKey(number)), ("repository", Repository(source)), ("url", url));

    public ChannelAddress? AddressForPullRequest(Source source, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var basis = new Uri(WebBase(source));
        if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), basis.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = basis.AbsolutePath.TrimEnd('/') + "/" + Repository(source) + (Github ? "/pull/" : "/pulls/");
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(uri.AbsolutePath[prefix.Length..].TrimEnd('/'), out var number) && number > 0 ? Address(source, number, url) : null;
    }

    private ChannelAddress? NotificationAddress(Source source, string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var repository = Regex.Escape(Repository(source));
        var match = Regex.Match(uri.AbsolutePath, @"/(?:repos/)?" + repository + @"/(?:issues|pulls?)/(\d+)/?$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) && number > 0 ? Address(source, number, url) : null;
    }

    public async Task<DeliveryOutcome> SendAsync(Source source, ChannelAddress address, string markdown, CancellationToken cancellationToken)
    {
        if (address.Kind != Id || address.Get("repository") != Repository(source)
            || !int.TryParse(address.Get("number"), out var number) || number <= 0 || address.Get("deliveryId") is not { Length: > 0 } delivery)
            throw new InvalidOperationException("代码托管回复地址或稳定投递 ID 无效。");
        var login = (await ApiAsync(source, "GET", "user", null, cancellationToken)).Data.String("login");
        if (string.IsNullOrWhiteSpace(login)) throw new InvalidOperationException("无法核对回复账号。");
        var marker = "<!-- dsh-superagent:" + delivery + " -->";
        var root = address.Get("reviewRootId");
        if (root is not null && (!long.TryParse(root, out var numeric) || numeric <= 0)) throw new InvalidOperationException("代码评论根 ID 无效。");
        // Gitea 1.24 has no GitHub-style inline-reply endpoint; retain context but reply in the PR conversation.
        if (!Github) root = null;
        if (root is not null)
        {
            try
            {
                var comment = (await ApiAsync(source, "GET", RepoPath(source, "pulls/comments/" + root), null, cancellationToken)).Data;
                root = comment.String("in_reply_to_id") ?? comment.String("id") ?? root;
            }
            catch (CodeHostException ex) when (ex.Status is 404 or 410) { root = null; }
        }
        var path = root is null ? $"issues/{number}/comments" : $"pulls/{number}/comments";
        try
        {
            await foreach (var response in PagesAsync(source, RepoPath(source, path), cancellationToken))
                if (response.Data.EnumerateArray().Any(item => string.Equals(item.Child("user").String("login"), login, StringComparison.OrdinalIgnoreCase)
                    && (root is null || item.String("in_reply_to_id") == root) && (item.String("body") ?? "").Contains(marker, StringComparison.Ordinal))) return DeliveryOutcome.Delivered;
            var target = root is null ? path : $"pulls/{number}/comments/{root}/replies";
            await ApiAsync(source, "POST", RepoPath(source, target), new { body = markdown + "\n\n" + marker }, cancellationToken);
        }
        catch (CodeHostException ex) when (ex.Status == 410 || (ex.Status == 403 && ex.Locked))
        {
            throw new ChannelDestinationGoneException("目标会话已删除或锁定，无法回复。");
        }

        return DeliveryOutcome.Delivered;
    }
}
