using AwesomeAssertions;

namespace Thalos.Tests.Unit;

public class SubagentAgentErrorTests
{
    [Fact]
    public void SubagentBudgetExceeded_carries_its_code_and_the_cap()
    {
        var error = AgentError.SubagentBudgetExceeded(5000);

        error.Code.Should().Be(AgentErrorCode.SubagentBudgetExceeded);
        error.Message.Should().Contain("5000");
    }

    [Fact]
    public void SubagentDepthExceeded_reports_both_depth_and_max()
    {
        var error = AgentError.SubagentDepthExceeded(depth: 4, max: 2);

        error.Code.Should().Be(AgentErrorCode.SubagentDepthExceeded);
        error.Message.Should().Contain("4").And.Contain("2");
    }

    [Fact]
    public void Existing_codes_keep_their_ordinal_values()
    {
        // the enum is serialized; appending must not renumber what came before
        ((int)AgentErrorCode.Validation).Should().Be(0);
        ((int)AgentErrorCode.AgentNotFound).Should().Be(1);
    }
}
