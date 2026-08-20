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

    private static async IAsyncEnumerable<AgentEvent> Stream(SessionId sessionId, TurnId turnId)
    {
        yield return new TextDeltaEvent(sessionId, turnId, "it ");
        yield return new TextDeltaEvent(sessionId, turnId, "changed");
        yield return new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "it changed", default, [], TimeSpan.Zero));
        await Task.CompletedTask;
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
