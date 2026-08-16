using Thalos.Runtime;

namespace Thalos.Tests.Unit.Runtime;

public sealed class AgentEventHubTests
{
    [Fact]
    public async Task Subscribers_receive_events_and_can_unsubscribe()
    {
        var hub = new AgentEventHub();
        var seen = new List<string>();
        ValueTask Handler(AgentEvent e, CancellationToken ct) { seen.Add(e.Kind); return default; }

        using (hub.Subscribe(Handler))
        {
            await hub.PublishAsync(new TextDeltaEvent(SessionId.New(), TurnId.New(), "a"), default);
        }
        await hub.PublishAsync(new TextDeltaEvent(SessionId.New(), TurnId.New(), "b"), default);

        seen.Should().Equal("text-delta");
        hub.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_is_a_no_op()
    {
        await new AgentEventHub().PublishAsync(new TextDeltaEvent(SessionId.New(), TurnId.New(), "a"), default);
    }
}
