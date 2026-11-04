using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Web.Data;
using DshSuperagent.Web.Pages.Admin.Threads;
using DshSuperagent.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DomainThread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Tests;

public sealed class ThreadDetailTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Detail_Shows_Latest_Disposition_Per_Trigger_Without_Deleting_History(int historyCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, new TestProtector());
        await db.Database.EnsureCreatedAsync();
        var source = Source.Create("test", "thread detail", []);
        var thread = DomainThread.Open(source.Id, "thread", "thread");
        var otherThread = DomainThread.Open(source.Id, "other", "other");
        var trigger = NewTrigger(source, thread, "history");
        var single = NewTrigger(source, thread, "single");
        var noDisposition = NewTrigger(source, thread, "none");
        var otherTrigger = NewTrigger(source, otherThread, "other");
        db.Sources.Add(source);
        db.Threads.AddRange(thread, otherThread);
        db.Triggers.AddRange(trigger, single, noDisposition, otherTrigger);
        var baseline = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var history = Enumerable.Range(0, historyCount)
            .Select(index => new Disposition(trigger.Id, thread.Id, DispositionKind.Evaluated, $"evaluation {index}"))
            .ToList();
        foreach (var item in history.AsEnumerable().Reverse())
        {
            db.Dispositions.Add(item);
            db.Entry(item).Property(value => value.CreatedAt).CurrentValue = baseline.AddMinutes(history.IndexOf(item));
        }

        var singleDisposition = new Disposition(single.Id, thread.Id, DispositionKind.Filtered, "single");
        var otherDisposition = new Disposition(otherTrigger.Id, otherThread.Id, DispositionKind.Stopped, "other thread");
        db.Dispositions.AddRange(singleDisposition, otherDisposition);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var model = new DetailModel(db, new EfPolicyRepository(db), new InMemoryWorkQueue());

        var result = await model.OnGetAsync(thread.Id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(3, model.Triggers.Count);
        Assert.Equal(historyCount == 0 ? 1 : 2, model.Dispositions.Count);
        Assert.Equal(singleDisposition.Id, model.Dispositions[single.Id].Id);
        Assert.False(model.Dispositions.ContainsKey(noDisposition.Id));
        Assert.False(model.Dispositions.ContainsKey(otherTrigger.Id));
        if (historyCount > 0)
        {
            Assert.Equal(history[^1].Id, model.Dispositions[trigger.Id].Id);
            Assert.Equal($"evaluation {historyCount - 1}", model.Dispositions[trigger.Id].Reason);
        }
        else
        {
            Assert.False(model.Dispositions.ContainsKey(trigger.Id));
        }

        Assert.Equal(historyCount + 2, await db.Dispositions.CountAsync());
    }

    [Fact]
    public async Task Equal_Timestamps_Use_Id_As_A_Stable_TieBreaker()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, new TestProtector());
        await db.Database.EnsureCreatedAsync();
        var source = Source.Create("test", "thread detail", []);
        var thread = DomainThread.Open(source.Id, "thread", "thread");
        var trigger = NewTrigger(source, thread, "trigger");
        var first = new Disposition(trigger.Id, thread.Id, DispositionKind.Filtered, "first");
        var second = new Disposition(trigger.Id, thread.Id, DispositionKind.Evaluated, "second");
        db.Sources.Add(source);
        db.Threads.Add(thread);
        db.Triggers.Add(trigger);
        db.Dispositions.AddRange(first, second);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        db.Entry(first).Property(value => value.CreatedAt).CurrentValue = timestamp;
        db.Entry(second).Property(value => value.CreatedAt).CurrentValue = timestamp;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var model = new DetailModel(db, new EfPolicyRepository(db), new InMemoryWorkQueue());

        await model.OnGetAsync(thread.Id, CancellationToken.None);

        var expectedId = first.Id.CompareTo(second.Id) > 0 ? first.Id : second.Id;
        Assert.Equal(expectedId, Assert.Single(model.Dispositions).Value.Id);
        await model.OnGetAsync(thread.Id, CancellationToken.None);
        Assert.Equal(expectedId, model.Dispositions[trigger.Id].Id);
        Assert.Equal(2, await db.Dispositions.CountAsync());
    }

    private static Trigger NewTrigger(Source source, DomainThread thread, string externalId) => Trigger.Record(
        source.Id, thread.Id, null, externalId, externalId, new TriggerActor("user", "User", ActorKind.Human),
        "text", true, TriggerStrength.Explicit, "chat", null, null, null, DateTimeOffset.UtcNow);

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
