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
        // here the caller's own token is what's cancelled, and the deadline is generous. This pins the
        // "!ct.IsCancellationRequested" guard: without it, this would be misreported as SubagentDeadlineExceeded
        // even though nothing timed out.
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
}
