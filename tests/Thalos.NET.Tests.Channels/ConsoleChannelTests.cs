using System.Text;
using Thalos.Channels.Console;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Channels;

public sealed class ConsoleChannelTests
{
    [Fact]
    public async Task Source_yields_one_message_per_line_and_stops_at_end_of_input()
    {
        var source = new ConsoleChannelSource(new StringReader("first\nsecond\n"), AnonymousSecurityContext.Instance);

        var messages = new List<InboundMessage>();
        await foreach (var m in source.ReadAsync(default))
        {
            messages.Add(m);
        }

        messages.Select(m => m.Text).Should().Equal("first", "second");
        messages.Should().OnlyContain(m => m.ChannelId == "console");
    }

    [Fact]
    public async Task Source_skips_blank_lines_so_a_stray_return_does_not_run_an_empty_turn()
    {
        var source = new ConsoleChannelSource(new StringReader("\n  \nreal\n"), AnonymousSecurityContext.Instance);

        var messages = new List<InboundMessage>();
        await foreach (var m in source.ReadAsync(default))
        {
            messages.Add(m);
        }

        messages.Should().ContainSingle().Which.Text.Should().Be("real");
    }

    [Fact]
    public async Task Adapter_appends_only_what_is_new_because_the_console_cannot_edit_in_place()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var conversationId = new ConversationId("c1");
        var turnId = TurnId.New();

        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnId, "Hello"), default);
        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnId, "Hello world"), default);

        output.ToString().Should().Be("Hello world");
    }

    [Fact]
    public async Task Adapter_writes_a_newline_when_the_turn_completes()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var conversationId = new ConversationId("c1");
        var turnId = TurnId.New();

        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnId, "done"), default);
        await adapter.DeliverAsync(conversationId, new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "done", default, [], TimeSpan.Zero)), default);

        output.ToString().Should().Be("done\n");
    }

    [Fact]
    public async Task Adapter_starts_the_next_turn_clean_after_the_previous_turn_completed()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var conversationId = new ConversationId("c1");
        var turnOneId = TurnId.New();
        var turnTwoId = TurnId.New();

        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnOneId, "Hel"), default);
        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnOneId, "Hello"), default);
        await adapter.DeliverAsync(conversationId, new TurnCompletedEvent(sessionId, turnOneId,
            new AgentTurnResult(turnOneId, sessionId, "Hello", default, [], TimeSpan.Zero)), default);

        // "He" is a prefix of turn 1's "Hello", not an extension of it. If the completed-turn reset were dropped,
        // this delta would be diffed against turn 1's leftover "Hello" instead of starting clean.
        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnTwoId, "He"), default);

        output.ToString().Should().Be("Hello\nHe");
    }
}
