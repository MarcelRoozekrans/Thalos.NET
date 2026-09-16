using Thalos.Tests.Channels.Fakes;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpBindFailureTests
{
    [Fact]
    public async Task Operator_is_told_when_the_conversation_map_fails_to_bind()
    {
        using var h = new PumpHarness();
        h.FailNextBind(AgentError.StoreError("bind failed"));

        await h.SendAndSettle("hello");

        h.Channel.Delivered.Should().NotBeEmpty(
            "the operator must always be told something, even when binding fails");
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull(
            "a bind failure must not leave the pump believing a binding was persisted");
    }
}
