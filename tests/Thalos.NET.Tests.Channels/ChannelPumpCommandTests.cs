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

        // The exact notice, not merely "something mentioning /agents": UnknownAgent and UnknownDefaultAgent both
        // say /agents, and the whole point of the pair is that they blame different people. A name the operator
        // typed is the operator's typo, so it must NOT log 604 either — that id means "DefaultAgent is wrong".
        h.Notices().Should().Contain(ChannelNotices.UnknownAgent);
        h.Logger.Entries.Should().NotContain(e => e.EventId.Id == 604);
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId.Should().Be(before);
    }

    [Fact]
    public async Task Bare_slash_new_with_an_unresolvable_DefaultAgent_reports_a_misconfiguration_and_logs_604()
    {
        // A bare /new falls back to Thalos:Channels:DefaultAgent. When THAT does not resolve, nobody typed a bad
        // name — the host is misconfigured — so telling the operator "I do not have an agent by that name" points
        // them at a mistake they did not make, and staying silent in the log leaves the one person who can fix it
        // with nothing. ResolveAsync already reports it this way on the identical condition; this is the same
        // condition reached through the other door.
        var h = new PumpHarness();
        h.Catalog.Agents.Returns([]);

        await h.SendAndSettle("/new");

        h.Notices().Should().Contain(ChannelNotices.UnknownDefaultAgent);
        h.Notices().Should().NotContain(ChannelNotices.UnknownAgent);
        h.Logger.Entries.Should().Contain(e => e.EventId.Id == 604);

        // Nothing was closed or rebound: an unresolvable agent must leave the conversation exactly as it was.
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
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

            // Cancelled is delivered from inside RunTrackedTurnAsync's catch, BEFORE its own finally unregisters
            // the conversation from the pump's /cancel-and-busy registry. Sending the follow-up immediately after
            // observing Cancelled would race that registry entry and could get Busy instead of a real turn —
            // wait for the unregistration too before sending it.
            await WaitUntilAsync(() => !h.Pump.IsTurnRunning(h.Channel.ChannelId, h.Channel.Conversation));

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

    [Fact]
    public async Task A_command_that_throws_is_caught_by_the_read_loop_and_logged()
    {
        // Targets PumpAsync's own per-message catch specifically. Command dispatch is the one path still awaited
        // inline on the read loop (see HandleAsync/StartTurnAsync) — an ordinary message's failure now runs on a
        // detached task PumpAsync never awaits, so THIS is the only remaining path that still proves the read
        // loop's own catch is reachable and doing its job, rather than every failure in the suite going through
        // RunTrackedTurnAsync's catch instead.
        var h = new PumpHarness();

        // Configured as ONE call with two funcs, not a throwing config followed by a second .Returns(...) to
        // "reset" it: NSubstitute records a property's .Returns(...) by invoking the getter, which would replay
        // the already-configured throw before the new configuration could ever attach. The first access throws;
        // every access after that returns the real list.
        h.Catalog.Agents.Returns(
            _ => throw new InvalidOperationException("simulated catalogue failure"),
            _ => [h.DefaultAgent, h.OtherAgent]);

        // SendAndSettle cannot be used: AgentsAsync throws before ever calling NotifyAsync, so nothing is ever
        // delivered for this message — the log entry is the only observable signal.
        await h.StartAndSend("/agents");
        await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.EventId.Id == 605));

        h.Logger.Entries.Should().Contain(e => e.EventId.Id == 605);

        // The read loop survived: the catalogue no longer throws on this second access, so a later /agents still
        // gets handled.
        await h.SendAndSettle("/agents");
        h.Notices().Should().Contain(n => n.Contains("daedalus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_turn_that_throws_is_caught_by_the_launched_task_and_logged()
    {
        // Targets RunTrackedTurnAsync's own catch-all specifically. An ordinary message's turn runs on a task the
        // read loop never awaits (that is the whole point of Task 9 round 2), so PumpAsync's per-message catch
        // cannot see this failure at all — RunTrackedTurnAsync's own catch-all, and the log entry it writes, are
        // the only thing standing between a raw exception here and a turn that silently vanishes.
        var h = new PumpHarness();
        h.NextTurnThrows();

        // SendAndSettle cannot be used: a throwing turn never delivers anything either.
        await h.StartAndSend("this blows up");
        await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.EventId.Id == 605));

        h.Logger.Entries.Should().Contain(e => e.EventId.Id == 605);
        h.Channel.Delivered.OfType<TurnFailedEvent>().Should().BeEmpty();
        h.Channel.Delivered.OfType<TurnCompletedEvent>().Should().BeEmpty();

        // The read loop survived: a later ordinary message still runs a turn to completion.
        await h.SendAndSettle("hello again");
        h.Channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_notify_that_throws_while_reporting_a_cancellation_is_caught_and_logged()
    {
        // Targets the INNER cancellation catch (cancelled while RunTurnAsync is actually running, the common
        // /cancel case) — its own TryNotifyAsync call is nested inside RunTrackedTurnAsync's outer try, so even
        // without its own guard a throw here happens to be safety-netted by the method's outer catch (Exception ex)
        // — see the OUTER-catch test below for the site that has no such net at all.
        var h = new PumpHarness();
        var gate = h.BlockNextTurn();

        try
        {
            await h.StartAndSend("blocking message");
            await WaitUntilAsync(() => h.Notices().Count >= 1);

            // The very next DeliverAsync call is the Cancelled notice itself.
            h.Channel.NextDeliverThrows(new InvalidOperationException("simulated adapter failure delivering Cancelled"));
            h.Channel.Send("/cancel");

            // 609, not 605: TryNotifyAsync has its own event id, so "the adapter could not deliver a notice" is
            // distinguishable in a log store from "handling the message blew up". Asserting 605 here would pass
            // just as well if this failure were routed through the generic handler, which is the whole point of
            // giving it an id of its own.
            await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.EventId.Id == 609));
            h.Logger.Entries.Should().Contain(e => e.EventId.Id == 609);
            h.Logger.Entries.Should().NotContain(e => e.EventId.Id == 605);

            // The read loop survived: a later ordinary message still runs a turn to completion.
            await h.SendAndSettle("hello again");
            h.Channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_notify_that_throws_while_cancelled_during_resolution_is_caught_and_logged()
    {
        // Targets the OUTER cancellation catch specifically — this is the exact site the review named: cancelled
        // during ResolveAsync, before a binding exists. This catch has NO further enclosing catch within
        // RunTrackedTurnAsync — if its own TryNotifyAsync call were unguarded and the adapter threw, the detached
        // task would fault with nothing to observe it, unlike the inner-catch case above.
        var h = new PumpHarness();
        var gate = h.BlockNextSessionCreation();

        try
        {
            await h.StartAndSend("first ever message");
            await WaitUntilAsync(() => h.Pump.IsTurnRunning(h.Channel.ChannelId, h.Channel.Conversation));

            h.Channel.NextDeliverThrows(new InvalidOperationException("simulated adapter failure delivering Cancelled"));
            h.Channel.Send("/cancel");

            // 609, not 605: TryNotifyAsync has its own event id, so "the adapter could not deliver a notice" is
            // distinguishable in a log store from "handling the message blew up". Asserting 605 here would pass
            // just as well if this failure were routed through the generic handler, which is the whole point of
            // giving it an id of its own.
            await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.EventId.Id == 609));
            h.Logger.Entries.Should().Contain(e => e.EventId.Id == 609);
            h.Logger.Entries.Should().NotContain(e => e.EventId.Id == 605);

            // The read loop survived: a later ordinary message still runs a turn to completion.
            await h.SendAndSettle("hello again");
            h.Channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_notice_with_no_session_is_still_addressed_to_the_conversation_that_asked()
    {
        // The defect this exists for: every operator notice used to be delivered against SessionId.New(), a
        // fabricated id bound to nothing. An adapter that has to resolve a chat from that id (Telegram) found
        // nothing and dropped the notice, so /help, /agents, the busy notice and "session ended" were all silence.
        // The conversation is what an adapter can actually address, and it is known here without any lookup.
        var h = new PumpHarness();
        await h.SendAndSettle("/help");

        // /help binds nothing — the point being that answering it must not require a session to exist.
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();

        lock (h.Channel.Delivered)
        {
            h.Channel.DeliveredTo.Should().Equal(h.Channel.Conversation);
            h.Channel.Delivered.OfType<TextDeltaEvent>().Should().ContainSingle()
                .Which.Text.Should().Be(ChannelNotices.Help);
        }
    }

    [Fact]
    public async Task A_notice_with_no_session_carries_no_session_id_rather_than_a_fabricated_one()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/help");

        // `default` is the assertion, not a placeholder: the old code put SessionId.New() here, so this fails
        // against it. An absent session is honest — an adapter can see there is nothing to correlate — where a
        // freshly minted id is indistinguishable from a real session that has simply gone missing.
        lock (h.Channel.Delivered)
        {
            h.Channel.Delivered.OfType<TextDeltaEvent>().Should().ContainSingle()
                .Which.SessionId.Should().Be(default(SessionId));
        }
    }

    [Fact]
    public async Task The_rebound_notice_reaches_the_operator_after_the_binding_is_cleared()
    {
        // HandleLifecycleFailureAsync unbinds and THEN notifies, so at the moment this notice is sent there is no
        // binding left to resolve a session through — it was unroutable by construction on a session-keyed seam.
        // It is also the notice that asks the operator to resend, so dropping it makes a message vanish silently.
        var h = new PumpHarness();
        await h.SendAndSettle("hello");

        h.NextTurnFails(AgentErrorCode.SessionNotFound);
        await h.SendAndSettle("hello again");

        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
        h.Notices().Should().Contain(ChannelNotices.Rebound);

        lock (h.Channel.Delivered)
        {
            h.Channel.DeliveredTo.Should().AllBeEquivalentTo(h.Channel.Conversation);
        }
    }

    [Fact]
    public async Task The_session_ended_notice_reaches_the_operator_after_the_binding_is_cleared()
    {
        // Same shape as the rebound case: EndAsync unbinds before it notifies.
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        await h.SendAndSettle("/end");

        h.Notices().Should().Contain(ChannelNotices.SessionEnded);

        lock (h.Channel.Delivered)
        {
            h.Channel.DeliveredTo.Should().AllBeEquivalentTo(h.Channel.Conversation);
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
