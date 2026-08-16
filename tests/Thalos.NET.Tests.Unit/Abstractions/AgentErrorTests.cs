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
    public void ToString_is_code_colon_message()
    {
        AgentError.Validation("Text is required").ToString().Should().Be("Validation: Text is required");
    }
}
