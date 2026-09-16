using AwesomeAssertions;
using NSubstitute;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentBudgetTests
{
    [Fact]
    public async Task A_turn_that_exceeds_the_token_budget_fails_the_run()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var overBudget = new AgentTurnResult(
            TurnId.New(), sessionId, "long answer", UsageOf(6_000), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(overBudget));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.SubagentBudgetExceeded);
        result.Error.Message.Should().Contain("5000");
    }

    [Fact]
    public async Task A_host_configured_default_budget_applies_when_the_request_omits_one()
    {
        // SubagentRunRequest.Budget is null here (Request() below is not given one), so the runner must fall back to
        // SubagentOptions.DefaultBudget - not the static SubagentBudget.Default (50,000 tokens). Usage of 2,000 is
        // well under SubagentBudget.Default but over this harness's configured 1,000-token DefaultBudget, and the
        // failure message names 1000, so a runner that used the static default instead of resolving the host's
        // configured one would report success (or, if it happened to fail, a message naming 50000) rather than this.
        var harness = SubagentRunnerHarness.Create();
        harness.Options.DefaultBudget = new SubagentBudget(1_000, TimeSpan.FromMinutes(10));
        var sessionId = SessionId.New();
        var overConfiguredDefault = new AgentTurnResult(
            TurnId.New(), sessionId, "answer", UsageOf(2_000), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(overConfiguredDefault));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(budget: null));

        result.ShouldBeFailureWith(AgentErrorCode.SubagentBudgetExceeded);
        result.Error.Message.Should().Contain("1000");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_max_total_tokens_is_rejected_without_creating_a_session(int maxTotalTokens)
    {
        // Zero or negative must be refused by ValidateRequest before any session is created - otherwise the session
        // is created and the turn is run (spending exactly the money this guard exists to prevent) only to fail
        // afterwards once the post-hoc token check in RunTurnAsync compares usage against a cap that can never be
        // satisfied.
        var harness = SubagentRunnerHarness.Create();
        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(maxTotalTokens, TimeSpan.FromMinutes(10)));

        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.Validation);
        result.Error.Message.Should().Contain("token budget must be positive");
        await harness.Runtime.DidNotReceive().CreateSessionAsync(
            Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_turn_inside_the_budget_succeeds()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var withinBudget = new AgentTurnResult(
            TurnId.New(), sessionId, "short answer", UsageOf(100), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(withinBudget));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_turn_landing_exactly_on_the_token_budget_succeeds()
    {
        // SubagentRunner compares with strict ">", so a run whose usage equals MaxTotalTokens exactly must be
        // allowed. This pins that boundary against a future refactor flipping it to ">=".
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var exactlyAtBudget = new AgentTurnResult(
            TurnId.New(), sessionId, "answer", UsageOf(5_000), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(exactlyAtBudget));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.IsSuccess.Should().BeTrue();
    }

    // TurnUsage is (int InputTokens, int OutputTokens, string ModelId) — verified against
    // src/Thalos.NET.Abstractions/Turns/TurnUsage.cs. The third parameter is required.
    private static TurnUsage UsageOf(int totalTokens) => new(totalTokens, 0, "test-model");
}
