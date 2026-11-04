using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Web.Data;
using DshSuperagent.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Tests;

public sealed class WorkRecoveryTests
{
    [Fact]
    public async Task Sqlite_Recovers_Only_Stale_Sending_Messages_And_Persists_The_Change()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, new TestProtector());
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var old = NewMessage();
        var recent = NewMessage();
        var missingTime = NewMessage();
        var pending = NewMessage();
        var sent = NewMessage();
        old.BeginSending();
        recent.BeginSending();
        missingTime.BeginSending();
        sent.MarkSent();
        db.ChannelMessages.AddRange(old, recent, missingTime, pending, sent);
        db.Entry(old).Property(message => message.SendingAt).CurrentValue = now.AddMinutes(-10);
        db.Entry(recent).Property(message => message.SendingAt).CurrentValue = now.AddMinutes(-1);
        db.Entry(missingTime).Property(message => message.SendingAt).CurrentValue = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var recovered = await WorkRecovery.RecoverStuckMessagesAsync(db, CancellationToken.None);

        Assert.Equal(1, recovered);
        db.ChangeTracker.Clear();
        var messages = await db.ChannelMessages.ToDictionaryAsync(message => message.Id);
        Assert.Equal(MessageStatus.Pending, messages[old.Id].Status);
        Assert.Null(messages[old.Id].SendingAt);
        Assert.True(messages[old.Id].NextAttemptAt >= now);
        Assert.Equal(MessageStatus.Sending, messages[recent.Id].Status);
        Assert.Equal(now.AddMinutes(-1), messages[recent.Id].SendingAt);
        Assert.Equal(MessageStatus.Sending, messages[missingTime.Id].Status);
        Assert.Equal(MessageStatus.Pending, messages[pending.Id].Status);
        Assert.Equal(MessageStatus.Sent, messages[sent.Id].Status);
        Assert.Equal(0, await WorkRecovery.RecoverStuckMessagesAsync(db, CancellationToken.None));
    }

    private static ChannelMessage NewMessage() => new(Guid.NewGuid(), null, MessagePurpose.Notice, "recovery test");

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
