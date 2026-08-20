using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class InboundMessageTests
{
    [Fact]
    public void ConversationId_round_trips_a_string()
    {
        var id = new ConversationId("123456789");
        id.Value.Should().Be("123456789");
        id.ToString().Should().Be("123456789");
    }

    [Fact]
    public void InboundMessage_carries_the_caller_the_channel_supplied()
    {
        var caller = AnonymousSecurityContext.Instance;
        var message = new InboundMessage("telegram", new ConversationId("42"), "hello", caller, "17");

        message.ChannelId.Should().Be("telegram");
        message.ConversationId.Value.Should().Be("42");
        message.Text.Should().Be("hello");
        message.Caller.Should().BeSameAs(caller);
        message.ExternalMessageId.Should().Be("17");
    }

    [Fact]
    public void ExternalMessageId_is_optional()
    {
        var message = new InboundMessage("console", new ConversationId("console"), "hi", AnonymousSecurityContext.Instance, null);
        message.ExternalMessageId.Should().BeNull();
    }
}
