using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Channels;
using Thalos.Channels.Console;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

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

    [Fact]
    public async Task Adapter_renders_the_whole_answer_from_the_terminal_event_not_the_last_delta()
    {
        // Directly pins the rule the end-to-end test below exercises through the real pump: the terminal event is
        // authoritative. Here the adapter is deliberately shown only the FIRST delta before the turn completes,
        // which is exactly what the coalescer hands it when the rest of the turn lands inside one flush interval.
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var conversationId = new ConversationId("c1");
        var turnId = TurnId.New();

        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnId, "Hello"), default);
        await adapter.DeliverAsync(conversationId, new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "Hello, world - and the tail nobody ever saw.", default, [], TimeSpan.Zero)), default);

        output.ToString().Should().Be("Hello, world - and the tail nobody ever saw.\n");
    }

    [Fact]
    public async Task Adapter_shows_a_failure_underneath_what_the_turn_had_already_printed()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var conversationId = new ConversationId("c1");
        var turnId = TurnId.New();

        await adapter.DeliverAsync(conversationId, new TextDeltaEvent(sessionId, turnId, "partial answer"), default);
        await adapter.DeliverAsync(conversationId, new TurnFailedEvent(sessionId, turnId,
            new AgentError(AgentErrorCode.ProviderError, "upstream exploded")), default);

        // The partial answer survives (a terminal cannot unprint it, and it is still useful), the failure is
        // reported rather than swallowed as a bare line break, and the line is closed.
        var written = output.ToString();
        written.Should().StartWith("partial answer\n\n");
        written.Should().Contain("ProviderError").And.Contain("upstream exploded");
        written.Should().EndWith("\n");
    }

    /// <summary>
    /// The end-to-end regression: <see cref="ConsoleChannelSource"/> to <see cref="ChannelPump"/> to
    /// <see cref="ConsoleChannelAdapter"/> on the <b>default</b> <see cref="ChannelOptions.FlushInterval"/>.
    /// <para>
    /// Every other pump test in this suite sets <c>FlushInterval = TimeSpan.Zero</c>, which makes the coalescer
    /// render every delta and hides this defect completely — which is precisely why it shipped green. The interval
    /// is host-wide and defaults to one second, so in any real console host the deltas after the first are
    /// suppressed and <c>TurnCompletedEvent.Result.Text</c> is the only event carrying the whole answer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_console_turn_arrives_in_full_under_the_default_flush_interval()
    {
        const string Answer = "Hello world, and every word after the very first delta.";

        var options = new ChannelOptions { DefaultAgent = "daedalus" };
        options.FlushInterval.Should().Be(TimeSpan.FromSeconds(1),
            "this test is only meaningful on the DEFAULT cadence — never set it to zero here");

        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var source = new ConsoleChannelSource(new StringReader("say hello\n"), AnonymousSecurityContext.Instance);

        var agent = new AgentDefinition { Id = AgentId.New(), Name = "daedalus", Instructions = "test" };
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns([agent]);

        var runtime = Substitute.For<IAgentRuntime>();
        runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result<SessionId, AgentError>.Success(SessionId.New()));
        runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Stream(call.Arg<AgentTurnRequest>().SessionId, Answer));

        using var pump = new ChannelPump([source], [adapter], runtime, catalog, new InMemoryConversationMap(),
            Options.Create(options), TimeProvider.System, NullLogger<ChannelPump>.Instance);

        await pump.StartAsync(default);

        // The StringReader runs dry after one line, which ends the reader loop; ExecuteAsync then drains the
        // detached turn task before it completes. Awaiting it is a real happens-before edge over every write the
        // adapter made, so the StringWriter is read single-threaded — no polling and no sleep anywhere.
        await pump.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        output.ToString().Should().Be(Answer + "\n");
    }

    // A real clock and no delay between the yields: the second delta lands microseconds after the first, well
    // inside the one-second interval, so the coalescer suppresses it exactly as it would on a fast production turn.
    private static async IAsyncEnumerable<AgentEvent> Stream(
        SessionId sessionId, string answer, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var turnId = TurnId.New();
        const int Cut = 13;   // "Hello world, " — the only render the coalescer lets through

        yield return new TextDeltaEvent(sessionId, turnId, answer[..Cut]);
        yield return new TextDeltaEvent(sessionId, turnId, answer[Cut..]);
        yield return new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, answer, default, [], TimeSpan.Zero));
    }
}
