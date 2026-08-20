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
        var turnId = TurnId.New();

        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "Hello"), default);
        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "Hello world"), default);

        output.ToString().Should().Be("Hello world");
    }

    [Fact]
    public async Task Adapter_writes_a_newline_when_the_turn_completes()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "done"), default);
        await adapter.DeliverAsync(sessionId, new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "done", default, [], TimeSpan.Zero)), default);

        output.ToString().Should().Be("done\n");
    }
}
