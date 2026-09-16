using AwesomeAssertions;
using NSubstitute;
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentBudgetExceeded);
        result.Error.Message.Should().Contain("5000");
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

    // TurnUsage is (int InputTokens, int OutputTokens, string ModelId) — verified against
    // src/Thalos.NET.Abstractions/Turns/TurnUsage.cs. The third parameter is required.
    private static TurnUsage UsageOf(int totalTokens) => new(totalTokens, 0, "test-model");
}
