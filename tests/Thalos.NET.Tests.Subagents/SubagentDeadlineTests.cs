using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentDeadlineTests
{
    [Fact]
    public async Task The_turn_is_cancelled_when_the_deadline_passes()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        // the turn observes its token and reports cancellation, as a real provider call would
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   var token = call.Arg<CancellationToken>();
                   return token.IsCancellationRequested
                       ? Result<AgentTurnResult, AgentError>.Failure(AgentError.Cancelled())
                       : Result<AgentTurnResult, AgentError>.Success(
                           new AgentTurnResult(TurnId.New(), sessionId, "too late", default, [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(1)));
        var result = await harness.Build().RunAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentDeadlineExceeded);
    }

    [Fact]
    public async Task A_caller_cancellation_is_reported_as_cancelled_not_a_deadline_breach()
    {
        // Same shape as the deadline test above (the turn observes a cancelled token and reports Cancelled), but
        // here only the caller's own token is cancelled, and the deadline (10 minutes) never elapses. This pins
        // that a failed turn is not reported as SubagentDeadlineExceeded merely because it failed while linked to a
        // deadline source - deadlineSource.IsCancellationRequested must actually be true, catching a naive "any
        // failure after linking counts as a deadline breach" bug.
        //
        // It does NOT exercise the "!ct.IsCancellationRequested" guard on SubagentRunner.RunAsync: since the
        // deadline never fires here, deadlineSource.IsCancellationRequested is already false and the "&&" short-
        // circuits before the guard is evaluated. That guard - the caller-cancellation-races-the-deadline case - is
        // pinned separately by A_caller_cancellation_racing_the_deadline_is_still_reported_as_cancelled below, which
        // advances the clock past the deadline AND cancels the caller in the same callback so both flags are true
        // when the check runs.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        using var callerCts = new CancellationTokenSource();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   callerCts.Cancel();
                   var token = call.Arg<CancellationToken>();
                   return token.IsCancellationRequested
                       ? Result<AgentTurnResult, AgentError>.Failure(AgentError.Cancelled())
                       : Result<AgentTurnResult, AgentError>.Success(
                           new AgentTurnResult(TurnId.New(), sessionId, "too late", default, [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request, callerCts.Token);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Cancelled);
    }

    [Fact]
    public async Task A_caller_cancellation_racing_the_deadline_is_still_reported_as_cancelled()
    {
        // This is the case the "!ct.IsCancellationRequested" guard in SubagentRunner.RunAsync actually exists for:
        // the wall clock has already passed the deadline (deadlineSource.IsCancellationRequested is true) AND the
        // caller also cancelled before/while the runtime observed the linked token (ct.IsCancellationRequested is
        // true too). Both flags are forced true in the same callback so the guard's short-circuit from the previous
        // test cannot apply here. When both are true, the caller's own cancellation must still win: it asked to
        // stop, so this must not be reported as a runaway-agent deadline breach.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        using var callerCts = new CancellationTokenSource();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   callerCts.Cancel();
                   var token = call.Arg<CancellationToken>();
                   return token.IsCancellationRequested
                       ? Result<AgentTurnResult, AgentError>.Failure(AgentError.Cancelled())
                       : Result<AgentTurnResult, AgentError>.Success(
                           new AgentTurnResult(TurnId.New(), sessionId, "too late", default, [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(1)));
        var result = await harness.Build().RunAsync(request, callerCts.Token);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Cancelled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_deadline_is_rejected_without_creating_a_session(int deadlineSeconds)
    {
        var harness = SubagentRunnerHarness.Create();
        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromSeconds(deadlineSeconds)));

        var result = await harness.Build().RunAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        await harness.Runtime.DidNotReceive().CreateSessionAsync(
            Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }
}
