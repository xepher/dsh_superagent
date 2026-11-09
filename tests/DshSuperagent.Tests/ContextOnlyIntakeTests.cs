using DshSuperagent.Core.Application;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using DshSuperagent.Providers.Common;
using DshSuperagent.Providers.GitHub;
using DshSuperagent.Web.Data;
using DshSuperagent.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DomainThread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Tests;

public sealed class ContextOnlyIntakeTests
{
    [Theory]
    [InlineData("alice", ActorKind.Human, true, false)]
    [InlineData("guest", ActorKind.Human, false, false)]
    [InlineData("unlisted-bot", ActorKind.Bot, false, false)]
    [InlineData("alice", ActorKind.Human, true, true)]
    public async Task Context_only_subscription_respects_gate_and_never_executes_stop(string sender, ActorKind kind, bool admitted, bool ended)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var protector = new Protector();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, protector);
        await db.Database.EnsureCreatedAsync();
        var source = Source.Create("github", "context intake", new Dictionary<string, string>());
        var origin = DomainThread.Open(source.Id, "origin", "origin");
        var discussion = DomainThread.Open(source.Id, "github:1", "generated PR");
        var policy = Policy.CreateDefault();
        policy.UpdateSourcePolicy(source.Id, [new AllowedUser("alice")], [], new AdmissionTable(), false);
        var mandate = Mandate.Grant(policy.Version, TriggerStrength.Explicit, "Fix", [],
            new TriggerActor("alice", "alice", ActorKind.Human), true, source.Id, "repo", MandateBasis.ExplicitRequest);
        var ticket = Ticket.Accept("existing fix", source.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), origin.Id, null, WorkKind.Fix, mandate);
        discussion.TrySubscribe(ticket.Id, contextOnly: true);
        if (ended) ticket.End(TicketOutcome.Completed);
        db.Sources.Add(source);
        db.Policies.Add(policy);
        db.Threads.AddRange(origin, discussion);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        var before = ticket.Inputs.Count;
        var registry = new SourceProviderRegistry([new GitHubSourceProvider(new NoCommands())]);
        var service = new InboundIngestionService(registry, new EfSourceRepository(db), new EfPolicyRepository(db),
            new EfThreadRepository(db), new EfTicketRepository(db), new EfChannelRepository(db),
            new EfConfigurationStore(db, protector), new NoStreams(), new InMemoryWorkQueue(), new EfDomainEventSink(db), new EfUnitOfWork(db));
        var inbound = new InboundEvent("review:1", "github:1", "PR", new TriggerActor(sender, sender, kind), "/stop",
            true, false, "repo", ChannelAddress.Create("github", ("number", "1")));

        var result = await service.HandleEventAsync(source.Id, inbound, default);

        Assert.Equal(200, result.StatusCode);
        db.ChangeTracker.Clear();
        var stored = await db.Tickets.Include(item => item.Inputs).SingleAsync();
        Assert.Equal(before + (admitted && !ended ? 1 : 0), stored.Inputs.Count);
        Assert.Equal(ended ? TicketStatus.Ended : TicketStatus.Open, stored.Status);
        Assert.True(stored.Mandate.IsActive);
        Assert.Empty(await db.Evals.ToListAsync());
        var disposition = await db.Dispositions.SingleAsync();
        Assert.Equal(admitted && !ended ? DispositionKind.Admitted : DispositionKind.Filtered, disposition.Kind);
        if (ended) Assert.Contains("没有开放工单", disposition.Reason);
        // Duplicate delivery is idempotent for both admitted and filtered events.
        await service.HandleEventAsync(source.Id, inbound, default);
        Assert.Equal(1, await db.Triggers.CountAsync());
        Assert.Equal(before + (admitted && !ended ? 1 : 0), (await db.Tickets.Include(item => item.Inputs).SingleAsync()).Inputs.Count);
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
    private sealed class NoCommands : IProviderCommandRunner
    {
        public Task<string> RunAsync(ProviderCommand command, CancellationToken token) => throw new InvalidOperationException("No real CLI allowed.");
        public Task WatchAsync(ProviderCommand command, Func<string, CancellationToken, Task> receive,
            Func<string, CancellationToken, Task> diagnostic, CancellationToken token) => throw new InvalidOperationException("No real CLI allowed.");
    }
    private sealed class NoStreams : IStreamReplyStore
    {
        public Task<StreamReplyState?> GetAsync(string id, CancellationToken token) => throw new InvalidOperationException("No stream in this scenario.");
        public Task SetRunningAsync(string id, string content, CancellationToken token) => throw new InvalidOperationException("No stream in this scenario.");
        public Task CompleteAsync(string id, string content, CancellationToken token) => throw new InvalidOperationException("No stream in this scenario.");
        public Task FinishAsync(string id, CancellationToken token) => throw new InvalidOperationException("No stream in this scenario.");
    }
}
