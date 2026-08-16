using Thalos.Runtime;

namespace Thalos.Tests.Unit.Runtime;

public sealed class AgentEventHubTests
{
    private static TextDeltaEvent Delta(string text) => new(SessionId.New(), TurnId.New(), text);

    [Fact]
    public async Task Subscribers_receive_events_and_can_unsubscribe()
    {
        var hub = new AgentEventHub();
        var seen = new List<string>();
        ValueTask Handler(AgentEvent e, CancellationToken ct) { seen.Add(e.Kind); return default; }

        using (hub.Subscribe(Handler))
        {
            await hub.PublishAsync(Delta("a"), default);
        }
        await hub.PublishAsync(Delta("b"), default);

        seen.Should().Equal("text-delta");
        hub.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_is_a_no_op()
    {
        await new AgentEventHub().PublishAsync(Delta("a"), default);
    }

    [Fact]
    public async Task Synchronously_throwing_subscriber_does_not_affect_others_or_the_publisher()
    {
        var hub = new AgentEventHub();
        var seen = new List<string>();
        using var _ = hub.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        using var __ = hub.Subscribe((e, _) => { seen.Add(((TextDeltaEvent)e).Text); return default; });

        var publish = async () => await hub.PublishAsync(Delta("a"), default);

        await publish.Should().NotThrowAsync();
        seen.Should().Equal("a");
    }

    [Fact]
    public async Task Asynchronously_faulting_subscriber_does_not_affect_others_or_the_publisher()
    {
        var hub = new AgentEventHub();
        var seen = new List<string>();
        using var _ = hub.Subscribe(async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); });
        using var __ = hub.Subscribe(async (e, _) => { await Task.Yield(); seen.Add(((TextDeltaEvent)e).Text); });

        var publish = async () => await hub.PublishAsync(Delta("a"), default);

        await publish.Should().NotThrowAsync();
        seen.Should().Equal("a");
    }

    [Fact]
    public async Task Unsubscribing_a_throwing_subscriber_removes_it()
    {
        var hub = new AgentEventHub();
        var calls = 0;
        var token = hub.Subscribe((_, _) => { calls++; throw new InvalidOperationException("boom"); });
        await hub.PublishAsync(Delta("a"), default);

        token.Dispose();
        await hub.PublishAsync(Delta("b"), default);

        calls.Should().Be(1);
        hub.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task Same_delegate_subscribed_twice_is_two_independent_subscriptions()
    {
        var hub = new AgentEventHub();
        var calls = 0;
        ValueTask Handler(AgentEvent e, CancellationToken ct) { Interlocked.Increment(ref calls); return default; }

        var first = hub.Subscribe(Handler);
        var second = hub.Subscribe(Handler);
        hub.SubscriberCount.Should().Be(2);
        await hub.PublishAsync(Delta("a"), default);
        calls.Should().Be(2);

        first.Dispose();
        hub.SubscriberCount.Should().Be(1);
        second.Dispose();
        hub.SubscriberCount.Should().Be(0);
    }
}
