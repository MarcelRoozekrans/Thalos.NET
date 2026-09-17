using AwesomeAssertions;

namespace Thalos.Tests.Unit;

public class SubagentOptionsTests
{
    [Fact]
    public void ThalosOptions_exposes_subagent_defaults()
    {
        var options = new ThalosOptions();

        options.Subagents.MaxDepth.Should().Be(2);
        options.Subagents.DefaultBudget.Should().Be(SubagentBudget.Default);
    }
}
