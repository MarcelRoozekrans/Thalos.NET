using AwesomeAssertions;
using Thalos.Tests.Unit.Runtime;

namespace Thalos.Tests.Unit;

public class SubagentRunRequestTests
{
    [Fact]
    public void Budget_is_null_when_none_is_supplied_so_the_runner_can_fall_back_to_the_host_default()
    {
        // SubagentRunRequest itself no longer picks a default: SubagentBudget? left null is the signal the runner
        // (SubagentRunner.RunAsync) uses to resolve SubagentOptions.DefaultBudget instead. Defaulting it here to
        // SubagentBudget.Default would silently shadow a host-configured DefaultBudget on every request that
        // doesn't name its own budget - see SubagentOptions.DefaultBudget's remarks.
        var request = new SubagentRunRequest
        {
            AgentId = AgentId.New(),
            Task = "summarise the findings",
            Caller = new TestSecurityContext("schedule:test"),
        };

        request.Budget.Should().BeNull();
        request.Depth.Should().Be(0);
        request.ParentSessionId.Should().BeNull();
    }
}
