using System.Text.Json;
using DshSuperagent.Core.Application;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Web.Services;

/// <summary>One runtime per Source. Cursors live in Source.ReadPosition, not ephemeral process memory.</summary>
public sealed class SourceRuntime(IServiceScopeFactory scopes, Source source, IBackgroundSourceProvider provider) : ISourceRuntime
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _intakeLock = new(1, 1);
    private Dictionary<string, string>? _state;

    public async Task ReceiveAsync(InboundEvent inbound, CancellationToken cancellationToken)
    {
        await _intakeLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<InboundIngestionService>()
                .HandleEventAsync(source.Id, inbound, cancellationToken);
            if (result.StatusCode != 200) throw new InvalidOperationException("后台来源接收失败，读取游标未推进。");
        }
        finally { _intakeLock.Release(); }
    }

    public async Task<string?> ReadStateAsync(string key, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            await LoadStateAsync(cancellationToken);
            return _state!.GetValueOrDefault(key);
        }
        finally { _stateLock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadStatePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            await LoadStateAsync(cancellationToken);
            return _state!.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary();
        }
        finally { _stateLock.Release(); }
    }

    public async Task WriteStateAsync(string key, string? value, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            await LoadStateAsync(cancellationToken);
            var next = new Dictionary<string, string>(_state!);
            if (value is null) next.Remove(key); else next[key] = value;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await db.Sources.FirstOrDefaultAsync(item => item.Id == source.Id, cancellationToken);
            if (current is null || !current.Enabled) return;
            current.RecordReadPosition(JsonSerializer.Serialize(next));
            await db.SaveChangesAsync(cancellationToken);
            _state = next;
        }
        finally { _stateLock.Release(); }
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var position = await db.Sources.AsNoTracking().Where(item => item.Id == source.Id)
            .Select(item => item.ReadPosition).FirstOrDefaultAsync(cancellationToken);
        _state = ParseState(position);
    }

    public static Dictionary<string, string> ParseState(string? position)
    {
        if (string.IsNullOrWhiteSpace(position)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(position) ?? []; }
        catch (JsonException) { return []; } // Older internal Sources have a plain-text position.
    }

    public async Task<IReadOnlyList<SourceThread>> GetSubscribedThreadsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (provider is IPullRequestSourceProvider pullRequests)
        {
            var tickets = await db.Tickets.Where(item => item.Status == TicketStatus.Open)
                .Include(item => item.Reports).ToListAsync(cancellationToken);
            foreach (var ticket in tickets)
            {
                foreach (var url in ticket.Reports.Select(item => item.PullRequestUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct())
                {
                    var address = pullRequests.AddressForPullRequest(source, url!);
                    if (address?.Get("threadKey") is not { Length: > 0 } key) continue;
                    var thread = db.Threads.Local.FirstOrDefault(item => item.SourceId == source.Id && item.ExternalKey == key)
                        ?? await db.Threads.Include(item => item.Subscriptions)
                            .FirstOrDefaultAsync(item => item.SourceId == source.Id && item.ExternalKey == key, cancellationToken);
                    if (thread is null)
                    {
                        thread = Thread.Open(source.Id, key, $"{source.Name} #{address.Get("number")}");
                        await db.Threads.AddAsync(thread, cancellationToken);
                        await db.Channels.AddAsync(new Channel(thread.Id, source.Id, source.ProviderId,
                            address.ToJson(), ChannelPurpose.ChatReply), cancellationToken);
                    }
                    thread.TrySubscribe(ticket.Id, contextOnly: true);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        var all = await db.Threads.AsNoTracking().Where(item => item.SourceId == source.Id)
            .Include(item => item.Subscriptions).ToListAsync(cancellationToken);
        var liveIds = await db.Tickets.Where(item => item.Status == TicketStatus.Open)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        var live = liveIds.ToHashSet();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var followed = all.Where(item => item.Subscriptions.Any(subscription => subscription.IsLive)
            && (item.LastActivityAt >= cutoff || item.Subscriptions.Any(subscription => subscription.IsLive && live.Contains(subscription.TicketId)))).ToList();
        var ids = followed.Select(item => item.Id).ToList();
        var channels = await db.Channels.AsNoTracking().Where(item => ids.Contains(item.ThreadId)).ToListAsync(cancellationToken);
        return followed.Select(thread => new SourceThread(thread.ExternalKey, thread.CreatedAt,
            ChannelAddress.FromJson(channels.FirstOrDefault(channel => channel.ThreadId == thread.Id)?.AddressJson))).ToList();
    }

    public async Task ObservePullRequestAsync(string threadKey, bool merged, bool closed, string url, CancellationToken cancellationToken)
    {
        if ((!merged && !closed) || provider is not IPullRequestSourceProvider pullRequests) return;
        var expected = pullRequests.AddressForPullRequest(source, url);
        if (expected?.Get("threadKey") != threadKey) throw new InvalidOperationException("PR 生命周期超出来源仓库范围。");
        await _intakeLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidates = await db.Tickets.Where(item => item.Status == TicketStatus.Open)
                .Include(item => item.Reports).Include(item => item.Assignments).Include(item => item.Inputs).ToListAsync(cancellationToken);
            var selector = scope.ServiceProvider.GetRequiredService<TicketReplyChannelSelector>();
            foreach (var ticket in candidates.Where(item => item.Reports.Any(report => report.PullRequestUrl is { } link
                         && pullRequests.AddressForPullRequest(source, link)?.Get("threadKey") == threadKey)))
            {
                ticket.End(merged ? TicketOutcome.Completed : TicketOutcome.Dropped);
                var content = merged ? $"修复 PR 已合并，工单已完成：{url}" : $"修复 PR 已关闭且未合并，工单已结束：{url}";
                var replyChannel = await selector.ResolveAsync(ticket, ticket.Inputs, cancellationToken);
                if (replyChannel is null)
                {
                    // A lifecycle notice that cannot go anywhere is noise once the answer itself was
                    // delivered; only a ticket that never reached the requester is a delivery failure.
                    var answered = await db.ChannelMessages.AnyAsync(
                        message => message.TicketId == ticket.Id
                            && (message.Status == MessageStatus.Sent || message.Status == MessageStatus.AwaitingPull),
                        cancellationToken);
                    if (!answered)
                    {
                        ticket.MarkReplyUndeliverable("没有可用的应答地址：PR 状态通知无法送达。");
                    }
                }
                else
                {
                    await db.ChannelMessages.AddAsync(new ChannelMessage(replyChannel.Id, ticket.Id, MessagePurpose.Result, content), cancellationToken);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _intakeLock.Release(); }
    }

    public Task ReportAsync(string component, string status, string detail, CancellationToken cancellationToken) =>
        WriteStateAsync("health:" + component, JsonSerializer.Serialize(new
        {
            status, detail = detail.Length > 300 ? detail[..300] : detail, at = DateTimeOffset.UtcNow,
        }), cancellationToken);
}
