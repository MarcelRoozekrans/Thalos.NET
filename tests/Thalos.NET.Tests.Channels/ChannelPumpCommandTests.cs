using NSubstitute;
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpCommandTests
{
    [Fact]
    public async Task Slash_new_closes_the_old_session_and_binds_a_fresh_one()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        var first = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        await h.SendAndSettle("/new");
        var second = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        second.Should().NotBe(first);
        await h.Runtime.Received(1).CloseSessionAsync(first, Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_new_with_an_argument_resolves_that_agent_by_name()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/new reviewer");

        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.AgentId.Should().Be(h.OtherAgent.Id);
    }

    [Fact]
    public async Task Slash_new_with_an_unknown_agent_name_is_refused_and_leaves_the_binding_alone()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        var before = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        await h.SendAndSettle("/new nosuchagent");

        h.Notices().Should().Contain(n => n.Contains("/agents", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId.Should().Be(before);
    }

    [Fact]
    public async Task Slash_agents_lists_agent_names_not_ids()
    {
        // AgentId renders as a 26-char ULID; showing that to a human would be useless.
        var h = new PumpHarness();
        await h.SendAndSettle("/agents");

        h.Notices().Should().Contain(n => n.Contains("daedalus", StringComparison.Ordinal) && n.Contains("reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slash_end_unbinds_the_conversation()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        await h.SendAndSettle("/end");

        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Slash_status_on_an_unbound_conversation_says_so_rather_than_creating_one()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/status");

        h.Notices().Should().Contain(n => n.Contains("No active session", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_command_is_refused_and_never_sent_to_the_model()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/reboot");

        h.Notices().Should().Contain(n => n.Contains("/help", StringComparison.Ordinal));
        h.Runtime.DidNotReceive().RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_help_lists_the_commands_without_touching_the_runtime()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/help");

        h.Notices().Should().Contain(n => n.Contains("/new", StringComparison.Ordinal) && n.Contains("/cancel", StringComparison.Ordinal));
        h.Runtime.DidNotReceive().RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_cancel_with_nothing_running_reports_that_and_touches_nothing()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/cancel");

        h.Notices().Should().Contain(n => n.Contains("Nothing is running", StringComparison.Ordinal));
        h.Runtime.DidNotReceive().RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
        await h.Runtime.DidNotReceive().CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>());
        await h.Runtime.DidNotReceive().CloseSessionAsync(Arg.Any<SessionId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_cancel_stops_a_running_turn_and_the_channel_keeps_reading()
    {
        // Proves the /cancel fix end-to-end, from a SINGLE source: the read loop now starts an ordinary turn
        // without waiting for it, so it stays free to read the next message — including /cancel — while that turn
        // is still open. Cancelling a genuinely in-flight turn must not tear down the read loop either: if it did,
        // the message sent after /cancel below would never be handled and this test would time out rather than
        // fail an assertion.
        var h = new PumpHarness();
        var gate = h.BlockNextTurn();

        try
        {
            // SendAndSettle cannot be used here: it returns after the FIRST delivery, which for a blocked turn is
            // exactly the delta below, delivered before the turn actually blocks on the gate. Racing "settled"
            // against "blocked" is the wrong thing to synchronise on, so send directly and poll for the delta.
            await h.StartAndSend("blocking message");
            await WaitUntilAsync(() => h.Notices().Count >= 1);

            h.Channel.Send("/cancel");
            await WaitUntilAsync(() => h.Notices().Any(n => n.Contains("Cancelled", StringComparison.Ordinal)));

            h.Notices().Should().Contain(n => n.Contains("Cancelled", StringComparison.Ordinal));

            // The read loop must have survived: a later ordinary message is still handled to completion.
            var completedBefore = h.Channel.Delivered.OfType<TurnCompletedEvent>().Count();
            await h.SendAndSettle("hello again");
            await WaitUntilAsync(() => h.Channel.Delivered.OfType<TurnCompletedEvent>().Skip(completedBefore).Any());
        }
        finally
        {
            // A failing assertion above must not leave the blocked fake turn hanging and the suite stuck with it.
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_second_message_during_a_running_turn_gets_the_busy_notice()
    {
        // Edge 4, now reachable at the pump level: the read loop no longer blocks on a turn, so a second message
        // for the SAME conversation is dequeued while the first is still running, and StartTurnAsync's own
        // _running check reports Busy without starting a second turn — rather than a queued backlog the operator
        // never sees.
        var h = new PumpHarness();
        var gate = h.BlockNextTurn();

        try
        {
            await h.StartAndSend("blocking message");
            await WaitUntilAsync(() => h.Notices().Count >= 1);

            h.Channel.Send("second message");
            await WaitUntilAsync(() => h.Notices().Any(n => n.Contains("Still working", StringComparison.Ordinal)));

            h.Notices().Should().Contain(n => n.Contains("Still working", StringComparison.Ordinal));

            // Only the first message's turn ever reached the runtime — the second was refused before that.
            h.Runtime.Received(1).RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    // Polls instead of a fixed delay for the same reason PumpHarness.WaitForDeliveryAsync does: the pump runs on
    // its own background task, so a fixed sleep is either flaky (too short) or wastefully slow (too long).
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("condition was not met within 5s");
    }
}
