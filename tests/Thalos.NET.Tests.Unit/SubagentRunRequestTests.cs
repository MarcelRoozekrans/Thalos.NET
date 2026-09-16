using AwesomeAssertions;
using Thalos.Tests.Unit.Runtime;

namespace Thalos.Tests.Unit;

public class SubagentRunRequestTests
{
    [Fact]
    public void Default_budget_is_applied_when_none_is_supplied()
    {
        var request = new SubagentRunRequest
        {
            AgentId = AgentId.New(),
            Task = "summarise the findings",
            Caller = new TestSecurityContext("schedule:test"),
        };

        request.Budget.Should().Be(SubagentBudget.Default);
        request.Depth.Should().Be(0);
        request.ParentSessionId.Should().BeNull();
    }
}
