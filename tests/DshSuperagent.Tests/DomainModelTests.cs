using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Common;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Work;

namespace DshSuperagent.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Gate_Explicit_Mention_Of_Listed_Person_Is_Explicit()
    {
        var policy = Policy.CreateDefault();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([new AllowedUser("u1")], [], new AdmissionTable(), false);

        var result = policy.Gate(sourcePolicy, new TriggerActor("u1", "U1", ActorKind.Human), addressed: true, threadHasTicket: false);

        Assert.True(result.IsAdmitted);
        Assert.Equal(TriggerStrength.Explicit, result.Strength);
        Assert.Equal(GateBasis.ExplicitMention, result.Basis);
    }

    [Fact]
    public void Gate_Unlisted_Addressed_Is_Ignored_By_Default_Table()
    {
        var policy = Policy.CreateDefault();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([new AllowedUser("u1")], [], new AdmissionTable(), false);

        var result = policy.Gate(sourcePolicy, new TriggerActor("u2", "U2", ActorKind.Human), addressed: true, threadHasTicket: false);

        Assert.False(result.IsAdmitted);
        Assert.Equal(TriggerStrength.NotAdmitted, result.Strength);
    }

    [Fact]
    public void Gate_Explicit_Admission_Cell_Overrides_Unlisted_Membership()
    {
        var policy = Policy.CreateDefault();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([], [], new AdmissionTable(unlistedAddressed: AdmissionChoice.Explicit), false);

        var result = policy.Gate(sourcePolicy, new TriggerActor("guest", "Guest", ActorKind.Human), addressed: true, threadHasTicket: false);

        Assert.Equal(TriggerStrength.Explicit, result.Strength);
        Assert.Equal(GateBasis.ExplicitAdmissionCell, result.Basis);
    }

    [Fact]
    public void Gate_Unlisted_FollowUp_Is_Inferred_And_Respects_Accept_Mode()
    {
        var policy = Policy.CreateDefault();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([], [], new AdmissionTable(unlistedNotAddressed: AdmissionChoice.Inferred), false);
        policy.SetAcceptMode(AcceptMode.Auto);

        var inferred = policy.Gate(sourcePolicy, new TriggerActor("guest", "Guest", ActorKind.Human), addressed: false, threadHasTicket: true);
        Assert.Equal(TriggerStrength.Inferred, inferred.Strength);
        Assert.Equal(GateBasis.FollowUpUnlisted, inferred.Basis);

        policy.SetAcceptMode(AcceptMode.Off);
        policy.SetAcceptMode(AcceptMode.Off);
        var off = policy.Gate(sourcePolicy, new TriggerActor("guest", "Guest", ActorKind.Human), addressed: false, threadHasTicket: true);
        Assert.False(off.IsAdmitted);
    }

    [Fact]
    public void Gate_Unlisted_FollowUp_Is_Ignored_When_Admission_Cell_Is_Ignore()
    {
        var policy = Policy.CreateDefault();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([], [], new AdmissionTable(), false);
        policy.SetAcceptMode(AcceptMode.Auto);

        var result = policy.Gate(sourcePolicy, new TriggerActor("guest", "Guest", ActorKind.Human), addressed: false, threadHasTicket: true);

        Assert.False(result.IsAdmitted);
    }

    [Fact]
    public void Ticket_Ready_Without_PullRequest_Ends_Completed()
    {
        var ticket = NewTicket(WorkKind.Answer);
        var assignment = ticket.StartAssignment("test");
        ticket.BeginAssignment(assignment.Id);
        ticket.RecordReport(new Report(ticket.Id, assignment.Id, ReportKind.Ready, "answer"));

        Assert.Equal(TicketStatus.Ended, ticket.Status);
        Assert.Equal(TicketOutcome.Completed, ticket.Outcome);
        Assert.Equal(TicketStage.Ended, ticket.Stage);
    }

    [Fact]
    public void Ticket_Ready_With_PullRequest_Waits_For_Review()
    {
        var ticket = NewTicket(WorkKind.Fix);
        var assignment = ticket.StartAssignment("test");
        ticket.BeginAssignment(assignment.Id);
        ticket.RecordReport(new Report(ticket.Id, assignment.Id, ReportKind.Ready, "pr", pullRequestUrl: "https://example.test/pr/1"));

        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(TicketStage.PersonReview, ticket.Stage);
    }

    [Fact]
    public void Person_Stop_Withdraws_Mandate_And_Prevents_Resume()
    {
        var ticket = NewTicket(WorkKind.Answer);
        ticket.StopByPerson();

        Assert.Equal(TicketStatus.Ended, ticket.Status);
        Assert.Equal(TicketOutcome.Stopped, ticket.Outcome);
        Assert.False(ticket.Mandate.IsActive);
        Assert.Throws<DomainException>(() => ticket.AddInput(InputKind.Trigger, "again", resumeEnded: true));
    }

    [Fact]
    public void Ended_Ticket_Resumes_On_Person_FollowUp()
    {
        var ticket = NewTicket(WorkKind.Answer);
        var assignment = ticket.StartAssignment("test");
        ticket.BeginAssignment(assignment.Id);
        ticket.RecordReport(new Report(ticket.Id, assignment.Id, ReportKind.Ready, "answer"));
        Assert.Equal(TicketStatus.Ended, ticket.Status);

        ticket.AddInput(InputKind.Trigger, "follow up", resumeEnded: true);

        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.EndedAt);
        Assert.Equal(TicketStage.WorkingQueued, ticket.Stage);
    }

    [Fact]
    public void Revalidation_Explicit_Mandate_Passes_But_Inferred_Needs_Auto_And_Rules()
    {
        var policy = Policy.CreateDefault();
        var rule = policy.Rules.Single();
        var sourcePolicy = new SourcePolicy(Guid.NewGuid());
        sourcePolicy.Update([new AllowedUser("u1")], [], new AdmissionTable(), false);
        var source = Source.Create("test", "source", new Dictionary<string, string>());
        var mandate = Mandate.Grant(
            1,
            TriggerStrength.Inferred,
            "fix",
            [rule.Id],
            new TriggerActor("u1", "U1", ActorKind.Human),
            addressed: true,
            source.Id,
            "test",
            MandateBasis.AdmissionCell);

        policy.SetAcceptMode(AcceptMode.Auto);
        var ok = MandateRevalidator.Revalidate(mandate, source, policy, sourcePolicy, true);
        Assert.True(ok.Ok, ok.Reason);

        policy.SetAcceptMode(AcceptMode.Observe);
        var failed = MandateRevalidator.Revalidate(mandate, source, policy, sourcePolicy, true);
        Assert.False(failed.Ok);
    }

    [Fact]
    public void Eval_Recovery_Is_Bounded_And_Releases_Thread_After_Three_Attempts()
    {
        var eval = Eval.Start(Guid.NewGuid(), Guid.NewGuid(), 1, [Guid.NewGuid()]);
        eval.Begin();
        Assert.True(eval.TryRecoverAfterRestart("restart"));
        Assert.Equal(EvalStatus.Queued, eval.Status);

        eval.Begin();
        Assert.True(eval.TryRecoverAfterRestart("restart"));
        eval.Begin();
        Assert.False(eval.TryRecoverAfterRestart("restart"));
        Assert.Equal(EvalStatus.Unavailable, eval.Status);
    }

    [Fact]
    public void Interrupted_Assignment_Returns_To_Pending_And_Ticket_Queues_Again()
    {
        var ticket = NewTicket(WorkKind.Answer);
        var assignment = ticket.StartAssignment("test");
        ticket.BeginAssignment(assignment.Id);
        Assert.Equal(AssignmentStatus.Running, assignment.Status);

        Assert.True(ticket.RecoverAssignmentAfterRestart(assignment.Id));
        Assert.Equal(AssignmentStatus.Pending, assignment.Status);
        Assert.Equal(TicketStage.WorkingQueued, ticket.Stage);
    }

    [Fact]
    public void Channel_Message_Stops_After_Three_Failures_When_Dropped()
    {
        var message = new ChannelMessage(Guid.NewGuid(), Guid.NewGuid(), MessagePurpose.Answer, "answer");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            message.BeginSending();
            message.MarkFailed("transport failed", TimeSpan.Zero);
        }

        message.MarkDropped("连续失败 3 次");
        Assert.Equal(MessageStatus.Dropped, message.Status);
        Assert.Equal(3, message.Attempts);
        Assert.NotNull(message.DroppedAt);
    }

    private static Ticket NewTicket(WorkKind kind)
    {
        var policy = Policy.CreateDefault();
        var source = Source.Create("test", "source", new Dictionary<string, string>());
        var mandate = Mandate.Grant(
            policy.Version,
            TriggerStrength.Explicit,
            kind.ToString(),
            [],
            new TriggerActor("u1", "U1", ActorKind.Human),
            addressed: true,
            source.Id,
            "test",
            MandateBasis.ExplicitRequest);
        return Ticket.Accept(
            "test ticket",
            source.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            kind,
            mandate);
    }
}
