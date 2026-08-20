using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Thalos.Channels;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>Wires a pump over a <see cref="FakeChannel"/> with a substituted runtime and a controllable clock.</summary>
public sealed class PumpHarness : IDisposable
{
    private AgentErrorCode? _nextFailure;
    private bool _started;

    public FakeChannel Channel { get; } = new();

    public InMemoryConversationMap Map { get; } = new();

    public FakeTimeProvider Clock { get; } = new();

    public IAgentRuntime Runtime { get; } = Substitute.For<IAgentRuntime>();

    /// <summary>The agent the pump resolves "daedalus" to. AgentId is a ULID; the NAME is what config and /new carry.</summary>
    public AgentDefinition DefaultAgent { get; } = new()
    {
        Id = AgentId.New(),
        Name = "daedalus",
        Instructions = "test",
    };

    /// <summary>A second agent so /new &lt;name&gt; has something to switch to.</summary>
    public AgentDefinition OtherAgent { get; } = new()
    {
        Id = AgentId.New(),
        Name = "reviewer",
        Instructions = "test",
    };

    public IAgentCatalog Catalog { get; } = Substitute.For<IAgentCatalog>();

    public ChannelPump Pump { get; }

    public PumpHarness()
    {
        Catalog.Agents.Returns([DefaultAgent, OtherAgent]);
        Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result<SessionId, AgentError>.Success(SessionId.New()));

        Runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Emit(call.Arg<AgentTurnRequest>()));

        Pump = new ChannelPump([Channel], [Channel], Runtime, Catalog, Map,
            Options.Create(new ChannelOptions { DefaultAgent = "daedalus", FlushInterval = TimeSpan.Zero }),
            Clock, NullLogger<ChannelPump>.Instance);
    }

    /// <summary>Makes the next turn fail with <paramref name="code"/> instead of completing.</summary>
    public void NextTurnFails(AgentErrorCode code) => _nextFailure = code;

    /// <summary>Every plain-text body the pump delivered, in order.</summary>
    public IReadOnlyList<string> Notices()
    {
        lock (Channel.Delivered)
        {
            return [.. Channel.Delivered.OfType<TextDeltaEvent>().Select(e => e.Text)];
        }
    }

    /// <summary>
    /// Sends <paramref name="text"/> and waits for the pump to finish handling it.
    ///
    /// <c>BackgroundService.StartAsync</c> unconditionally does <c>Task.Run(() =&gt; ExecuteAsync(...))</c> on
    /// every call — it does not check whether the service is already running. Calling it once per message (as a
    /// naive harness might, mirroring how a test calls one API per assertion) would start a second, third, fourth...
    /// independent <c>ExecuteAsync</c> loop each racing the others to read the same <see cref="FakeChannel"/>, which
    /// would make every assertion in this file meaningless (multiple pumps double-handling — or racing to grab —
    /// the same message). So the pump is started exactly once, lazily, on the first send; every later call only
    /// enqueues and waits.
    /// </summary>
    public async Task SendAndSettle(string text)
    {
        if (!_started)
        {
            await Pump.StartAsync(default).ConfigureAwait(false);
            _started = true;
        }

        int before;
        lock (Channel.Delivered)
        {
            before = Channel.Delivered.Count;
        }

        Channel.Send(text);
        await WaitForDeliveryAsync(before).ConfigureAwait(false);
    }

    // Polls instead of a fixed delay: the pump delivers on its own reader-loop task, so a fixed sleep is either
    // too short (flaky) or wastefully long. A deadline turns "the pump never delivered" into a clear timeout
    // instead of a hang.
    private async Task WaitForDeliveryAsync(int countBefore)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (Channel.Delivered)
            {
                if (Channel.Delivered.Count > countBefore)
                {
                    return;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("the pump never delivered a response to the message within 5s");
    }

    private async IAsyncEnumerable<AgentEvent> Emit(AgentTurnRequest request)
    {
        var turnId = TurnId.New();
        if (_nextFailure is { } code)
        {
            _nextFailure = null;
            yield return new TurnFailedEvent(request.SessionId, turnId, new AgentError(code, code.ToString()));
            yield break;
        }

        yield return new TextDeltaEvent(request.SessionId, turnId, "ok");
        yield return new TurnCompletedEvent(request.SessionId, turnId,
            new AgentTurnResult(turnId, request.SessionId, "ok", default, [], TimeSpan.Zero));
        await Task.CompletedTask;
    }

    public void Dispose() => Pump.Dispose();
}
