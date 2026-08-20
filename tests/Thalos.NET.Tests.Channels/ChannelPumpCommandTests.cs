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
}
