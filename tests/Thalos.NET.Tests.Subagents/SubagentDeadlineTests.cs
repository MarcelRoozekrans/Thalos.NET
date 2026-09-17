using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Thalos.Tests.Subagents.Fakes;
using Thalos.Testing;
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

        // ShouldBeFailureWith's non-null-Message assertion is what actually pins this: AgentErrorCode.Validation is
        // enum member 0, so default(AgentError).Code is already Validation, and with the guard reverted the
        // unconfigured NSubstitute mock returns default(Result<AgentTurnResult, AgentError>) - a value whose
        // IsFailure and Code would pass this assertion without the guard ever running. default(AgentError) has a
        // null Message, so the explicit Contains check below on top of it cannot pass by struct luck either.
        result.ShouldBeFailureWith(AgentErrorCode.Validation);
        result.Error.Message.Should().Contain("Subagent deadline must be positive");
        await harness.Runtime.DidNotReceive().CreateSessionAsync(
            Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData((double)uint.MaxValue)] // uint.MaxValue ms itself is 1ms over the CancellationTokenSource ceiling
    [InlineData(double.MaxValue)] // TimeSpan.MaxValue.TotalMilliseconds: the "effectively no deadline" a caller would reach for
    public async Task A_deadline_above_the_CancellationTokenSource_ceiling_is_rejected_without_creating_a_session(double deadlineMilliseconds)
    {
        // new CancellationTokenSource(delay, timeProvider) throws ArgumentOutOfRangeException above uint.MaxValue - 1
        // milliseconds (~49.7 days) - confirmed empirically against both net8.0 and net10.0 in the course of this
        // fix. ValidateRequest must catch this before a session is created, or a caller reaching for "effectively no
        // deadline" via TimeSpan.MaxValue gets an unhandled exception from an API whose own XML doc promises an
        // AgentError instead.
        var harness = SubagentRunnerHarness.Create();
        var deadline = deadlineMilliseconds == double.MaxValue
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(deadlineMilliseconds);
        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, deadline));

        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.Validation);
        result.Error.Message.Should().Contain("must not exceed");
        await harness.Runtime.DidNotReceive().CreateSessionAsync(
            Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_turn_that_finishes_over_budget_after_the_deadline_elapsed_still_reports_the_budget_verdict()
    {
        // Regression: the deadline guard used to re-check turn.IsFailure after RunTurnAsync's own budget check may
        // already have synthesised SubagentBudgetExceeded, so a turn that completed over budget *and* past its
        // deadline reported SubagentDeadlineExceeded - silently overwriting the runner's own settlement. The turn
        // here succeeds from the runtime's point of view (no cancellation observed) but reports more usage than the
        // budget allows, and the wall clock has also elapsed by the time it comes back; the budget verdict must win
        // because Code is SubagentBudgetExceeded, not Cancelled.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   return Result<AgentTurnResult, AgentError>.Success(
                       new AgentTurnResult(TurnId.New(), sessionId, "answer", new TurnUsage(6_000, 0, "test-model"), [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(1)));
        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.SubagentBudgetExceeded);
    }

    [Fact]
    public async Task A_genuine_provider_error_landing_after_the_deadline_is_not_relabelled_as_a_deadline_breach()
    {
        // Same clobbering bug as above, the other source of a settled verdict: a real ProviderError the runtime
        // returns - unrelated to cancellation - must not be overwritten merely because the wall clock also happened
        // to have elapsed by the time it came back. Code is ProviderError here, not Cancelled.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   return Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("model exploded"));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(1)));
        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.ProviderError);
    }

    [Fact]
    public async Task A_turn_that_succeeds_after_its_deadline_elapsed_is_reported_as_success_but_logs_a_warning()
    {
        // "A deadline stops work, a budget settles it" (ISubagentRunner remarks): a turn that raced past its
        // deadline and still came back with a real result is correctly reported as a success - the deadline had
        // nothing left to stop. But that is otherwise invisible, so RunAsync must log a warning on this path.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var parentSessionId = SessionId.New();
        var logger = new CapturingLogger<SubagentRunner>();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   return Result<AgentTurnResult, AgentError>.Success(
                       new AgentTurnResult(TurnId.New(), sessionId, "just in time", new TurnUsage(10, 10, "test-model"), [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(1))) with
        {
            ParentSessionId = parentSessionId,
        };
        var result = await harness.Build(logger).RunAsync(request);

        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(sessionId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains(parentSessionId.ToString(), StringComparison.Ordinal));
    }
}
