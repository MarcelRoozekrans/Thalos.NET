namespace Thalos.Tests.Unit.Abstractions;

public sealed class AgentErrorTests
{
    [Fact]
    public void Factories_set_code_and_message()
    {
        var id = SessionId.New();
        var e = AgentError.SessionNotFound(id);

        e.Code.Should().Be(AgentErrorCode.SessionNotFound);
        e.Message.Should().Contain(id.ToString());
        e.Detail.Should().BeNull();
    }

    [Fact]
    public void Errors_with_same_code_and_message_are_equal()
    {
        AgentError.Cancelled().Should().Be(AgentError.Cancelled());
    }

    [Fact]
    public void Errors_with_different_detail_are_not_equal()
    {
        AgentError.StoreError("boom", "a").Should().NotBe(AgentError.StoreError("boom", "b"));
    }

    [Fact]
    public void ToString_is_code_colon_message()
    {
        AgentError.Validation("Text is required").ToString().Should().Be("Validation: Text is required");
    }

    [Fact]
    public void ToString_appends_detail_when_present()
    {
        AgentError.ToolDenied("roslyn__x", "not on allow-list").ToString()
            .Should().Be("ToolDenied: Tool 'roslyn__x' was denied. — not on allow-list");
    }
}
