using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpTurnTests
{
    [Fact]
    public async Task A_text_message_runs_a_turn_and_delivers_its_terminal_event()
    {
        var channel = new FakeChannel();
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        var runtime = Substitute.For<IAgentRuntime>();
        runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<SessionId, AgentError>.Success(sessionId));
        runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream(sessionId, turnId));

        using var pump = Build(channel, runtime);
        channel.Send("what changed?");
        await pump.StartAsync(default);
        await WaitForTerminal(channel);
        channel.Complete();
        await pump.StopAsync(default);

        channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();

        // RunTurnStreamingAsync returns IAsyncEnumerable<AgentEvent>, not a Task, so the verification call itself
        // (not something it returns) is what is checked — it must not be awaited.
        runtime.Received(1).RunTurnStreamingAsync(
            Arg.Is<AgentTurnRequest>(r => r.Text == "what changed?" && r.SessionId == sessionId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_that_throws_does_not_end_the_channel_read_loop()
    {
        var channel = new FakeChannel();
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        var runtime = Substitute.For<IAgentRuntime>();
        runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<SessionId, AgentError>.Success(sessionId));

        // First call's stream throws mid-enumeration (simulating a bad message); the second returns normally.
        // NSubstitute stays on the last configured value for any further calls.
        runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingStream(), Stream(sessionId, turnId));

        using var pump = Build(channel, runtime);

        // Different conversations: ordinary messages are no longer serialised by the read loop (see ChannelPump.
        // StartTurnAsync — fixed to stop /cancel and the busy notice being unreachable) — a second message for the
        // SAME conversation as a still-registered turn now gets Busy instead of running. This test has always been
        // about the read loop surviving a bad message for whatever comes after it, not about same-conversation
        // ordering, so the second message uses its own conversation to keep that assertion deterministic rather
        // than racing the unrelated per-conversation collision.
        channel.Send("this one blows up");
        channel.Send("this one should still land", new ConversationId("other-conversation"));
        await pump.StartAsync(default);
        await WaitForTerminal(channel);
        channel.Complete();
        await pump.StopAsync(default);

        // The first message's failure must not have ended the read loop: the second message still ran a turn
        // and delivered its terminal event, and no event at all came out of the first (failed) message.
        channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();
        runtime.Received(2).RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<AgentEvent> Stream(SessionId sessionId, TurnId turnId)
    {
        yield return new TextDeltaEvent(sessionId, turnId, "it ");
        yield return new TextDeltaEvent(sessionId, turnId, "changed");
        yield return new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "it changed", default, [], TimeSpan.Zero));
        await Task.CompletedTask;
    }

    // Throws once enumeration starts. The `foreach` over an always-empty array (never entered at runtime) is what
    // makes the compiler treat this as an iterator method; a bare `throw` with no `yield` anywhere would not compile
    // as an IAsyncEnumerable<T>, and a trailing `yield break` after an unconditional throw would be unreachable code
    // (a build error here, since warnings are errors).
    private static async IAsyncEnumerable<AgentEvent> ThrowingStream()
    {
        foreach (var never in Array.Empty<AgentEvent>())
        {
            yield return never;
        }

        await Task.Yield();
        throw new InvalidOperationException("simulated failure while streaming a turn");
    }

    private static ChannelPump Build(FakeChannel channel, IAgentRuntime runtime)
    {
        // AgentId is a ULID; configuration and /new name agents by AgentDefinition.Name, so the pump needs a catalogue
        // to resolve "daedalus" into an id. Substituting the catalogue is what makes that resolution testable.
        var definition = new AgentDefinition { Id = AgentId.New(), Name = "daedalus", Instructions = "test" };
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns([definition]);

        return new ChannelPump([channel], [channel], runtime, catalog, new InMemoryConversationMap(),
            Options.Create(new ChannelOptions { DefaultAgent = "daedalus", FlushInterval = TimeSpan.Zero }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelPump>.Instance);
    }

    // Polls rather than blocking on a single ValueTask because the pump delivers through DeliverAsync on its own
    // reader-loop task; a fixed deadline turns "the pump never delivered" into a clear timeout instead of a hang,
    // and the tight 10ms poll keeps the test fast on the (overwhelmingly common) path where the turn completes almost
    // immediately.
    private static async Task WaitForTerminal(FakeChannel channel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (channel.Delivered)
            {
                foreach (var delivered in channel.Delivered)
                {
                    if (delivered is TurnCompletedEvent or TurnFailedEvent)
                    {
                        return;
                    }
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no terminal event was delivered within 5s");
    }
}
