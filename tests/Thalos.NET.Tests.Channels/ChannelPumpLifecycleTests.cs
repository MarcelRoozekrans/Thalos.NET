using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpLifecycleTests
{
    [Fact]
    public async Task An_unbound_conversation_is_bound_implicitly_to_the_default_agent()
    {
        using var h = new PumpHarness();
        await h.SendAndSettle("hello");

        var binding = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value;
        binding.Should().NotBeNull();
        // AgentId is a ULID, so assert against the catalogue's definition id — not against the name "daedalus".
        binding!.AgentId.Should().Be(h.DefaultAgent.Id);
    }

    [Fact]
    public async Task An_idle_conversation_rolls_onto_a_new_session_and_says_so()
    {
        using var h = new PumpHarness();
        await h.SendAndSettle("first");
        var first = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        h.Clock.Advance(TimeSpan.FromHours(13));
        await h.SendAndSettle("second");

        var second = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;
        second.Should().NotBe(first);
        h.Notices().Should().Contain(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_bound_session_the_runtime_no_longer_knows_is_rebound_and_announced()
    {
        using var h = new PumpHarness();
        await h.SendAndSettle("first");

        h.NextTurnFails(AgentErrorCode.SessionNotFound);
        await h.SendAndSettle("second");

        // The binding is cleared and the operator is asked to resend — the message is NOT silently swallowed,
        // and it is NOT auto-retried either, because a retry against a runtime that just rejected the session
        // is how a rebind loop starts.
        h.Notices().Should().Contain(n => n.Contains("Send that message again", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task A_busy_session_is_told_to_cancel_rather_than_queued()
    {
        using var h = new PumpHarness();
        await h.SendAndSettle("first");

        h.NextTurnFails(AgentErrorCode.SessionBusy);
        await h.SendAndSettle("second");

        h.Notices().Should().Contain(n => n.Contains("/cancel", StringComparison.Ordinal));
    }
}
