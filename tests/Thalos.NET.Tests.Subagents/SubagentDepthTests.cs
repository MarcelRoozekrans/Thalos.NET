using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Authorization;

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
        var harness = SubagentRunnerHarness.Create();
        harness.Options.MaxDepth = 2;

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(depth: 2));

        result.Error.Code.Should().NotBe(AgentErrorCode.SubagentDepthExceeded);
    }
}
