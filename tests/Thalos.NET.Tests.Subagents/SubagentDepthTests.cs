using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentDepthTests
{
    [Fact]
    public async Task A_request_deeper_than_the_configured_maximum_is_refused_before_any_session_is_created()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Options.MaxDepth = 2;

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(depth: 3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentDepthExceeded);
        await harness.Runtime.DidNotReceive()
            .CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Depth_equal_to_the_maximum_is_allowed()
    {
        // Configured the same way the success paths in SubagentBudgetTests are, and asserting IsSuccess plus
        // Received(1) on CreateSessionAsync, because an unconfigured NSubstitute mock returns
        // default(Result<SessionId, AgentError>), whose Error.Code is Validation (enum member 0) - not
        // SubagentDepthExceeded. A bare "Code != SubagentDepthExceeded" assertion would pass on that default
        // failure too, proving only that depth wasn't the *reported* reason, not that the run was actually
        // allowed to reach session creation.
        var harness = SubagentRunnerHarness.Create();
        harness.Options.MaxDepth = 2;
        var sessionId = SessionId.New();
        var turnResult = new AgentTurnResult(
            TurnId.New(), sessionId, "answer", new TurnUsage(1, 1, "test-model"), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(turnResult));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(depth: 2));

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1)
            .CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }
}
