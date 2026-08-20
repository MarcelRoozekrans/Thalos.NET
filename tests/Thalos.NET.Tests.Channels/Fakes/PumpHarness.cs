using System.Runtime.CompilerServices;
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
    private bool _nextTurnThrows;
    private bool _started;
    private TaskCompletionSource? _blockingGate;
    private TaskCompletionSource? _blockingSessionGate;

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

    /// <summary>
    /// Captures every log call the pump makes instead of discarding it (<see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/>
    /// would). A turn now runs on a detached task nothing else awaits, so the log is the only observable signal
    /// that its own exception handling actually fired — a test asserting on delivered events alone cannot tell.
    /// </summary>
    public CapturingLogger<ChannelPump> Logger { get; } = new();

    public ChannelPump Pump { get; }

    public PumpHarness()
    {
        Catalog.Agents.Returns([DefaultAgent, OtherAgent]);
        Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateSessionResult(call.Arg<CancellationToken>()));

        Runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Emit(call.Arg<AgentTurnRequest>(), call.Arg<CancellationToken>()));

        Pump = new ChannelPump([Channel], [Channel], Runtime, Catalog, Map,
            Options.Create(new ChannelOptions { DefaultAgent = "daedalus", FlushInterval = TimeSpan.Zero }),
            Clock, Logger);
    }

    /// <summary>Makes the next turn fail with <paramref name="code"/> instead of completing.</summary>
    public void NextTurnFails(AgentErrorCode code) => _nextFailure = code;

    /// <summary>
    /// Makes the NEXT turn started against this harness throw a raw exception mid-stream, instead of failing
    /// through the normal <see cref="TurnFailedEvent"/> lifecycle path <see cref="NextTurnFails"/> models. This is
    /// what a genuinely buggy tool or provider call looks like to <c>RunTrackedTurnAsync</c>'s own catch-all, as
    /// opposed to a <see cref="AgentErrorCode"/> the runtime reports on purpose.
    /// </summary>
    public void NextTurnThrows() => _nextTurnThrows = true;

    /// <summary>
    /// Makes the NEXT turn started against this harness deliver its first event and then block — still "running",
    /// still registered against the pump's <c>/cancel</c> registry — until the returned source is completed. Lets a
    /// test drive a genuinely in-flight turn (for mid-turn <c>/cancel</c>), rather than the fake's normal
    /// instantaneous completion. Callers must complete the source (ideally in a <c>finally</c>) even on test
    /// failure, or the pump's background turn is left blocked forever.
    /// </summary>
    public TaskCompletionSource BlockNextTurn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blockingGate = gate;
        return gate;
    }

    /// <summary>
    /// Makes the NEXT <c>CreateSessionAsync</c> call — i.e. the next implicit-bind or idle-rollover resolution —
    /// block until the returned source completes. Lets a test cancel /cancel WHILE resolution is still in
    /// progress, before any binding exists, rather than only while an already-resolved turn is running. Callers
    /// must complete the source (ideally in a <c>finally</c>) even on test failure.
    /// </summary>
    public TaskCompletionSource BlockNextSessionCreation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blockingSessionGate = gate;
        return gate;
    }

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

        // The first delivery is not the same as "done": an ordinary message's turn runs on a detached task (see
        // ChannelPump.StartTurnAsync), and its Cancelled/terminal notice is sent from inside RunTrackedTurnAsync's
        // catch BEFORE its own finally clears the conversation from the pump's /cancel-and-busy registry. A test
        // that calls SendAndSettle again right after would otherwise race that registry entry and could get Busy
        // instead of a real turn. Waiting for it to clear here makes every SendAndSettle call in this file safe to
        // follow immediately with another one, without each test having to reason about that window itself.
        await WaitForNotRunningAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the pump (if not already started, same guard as <see cref="SendAndSettle"/>) and enqueues
    /// <paramref name="text"/> WITHOUT waiting for any delivery. For a turn set up with <see cref="BlockNextTurn"/>,
    /// <see cref="SendAndSettle"/> cannot be used to synchronise on "the turn is now running and blocked" — its
    /// first event can be (and is) delivered before the turn actually blocks, so waiting on delivery alone would
    /// race the very thing the caller wants to observe. Callers should instead poll <see cref="Notices"/> or
    /// <see cref="Channel"/>.<see cref="FakeChannel.Delivered"/> for the specific event they are waiting on.
    /// </summary>
    public async Task StartAndSend(string text)
    {
        if (!_started)
        {
            await Pump.StartAsync(default).ConfigureAwait(false);
            _started = true;
        }

        Channel.Send(text);
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

    // Polls Pump.IsTurnRunning (internal, test-only) rather than Delivered: a command message never registers in
    // _running at all, so this resolves immediately for those, and only actually waits for an ordinary message's
    // detached turn task to finish removing itself.
    private async Task WaitForNotRunningAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!Pump.IsTurnRunning(Channel.ChannelId, Channel.Conversation))
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("the pump never finished the turn (conversation still registered as running) within 5s");
    }

    // NSubstitute's ValueTask<T> support only takes a SYNCHRONOUS Func<CallInfo, T> (no async/Task/ValueTask
    // overload exists for it), so a genuinely awaited gate is not an option here the way BlockNextTurn's Emit
    // uses one. Blocking the calling (thread-pool) thread instead still lets cancellation interrupt it exactly
    // like a real await would: WaitAsync(ct).GetAwaiter().GetResult() observes ct and throws synchronously,
    // unwound by the caller exactly the same way an awaited OperationCanceledException would be.
    private Result<SessionId, AgentError> CreateSessionResult(CancellationToken ct)
    {
        if (_blockingSessionGate is { } gate)
        {
            _blockingSessionGate = null;
            gate.Task.WaitAsync(ct).GetAwaiter().GetResult();
        }

        return Result<SessionId, AgentError>.Success(SessionId.New());
    }

    // ct is honoured (not just accepted) so a blocked turn (see BlockNextTurn) reacts to /cancel exactly as a real
    // streaming call would — without this, cancelling the linked token would have no observable effect on the fake.
    private async IAsyncEnumerable<AgentEvent> Emit(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var turnId = TurnId.New();
        if (_nextTurnThrows)
        {
            _nextTurnThrows = false;
            await Task.Yield();
            throw new InvalidOperationException("simulated failure while streaming a turn");
        }

        if (_nextFailure is { } code)
        {
            _nextFailure = null;
            yield return new TurnFailedEvent(request.SessionId, turnId, new AgentError(code, code.ToString()));
            yield break;
        }

        yield return new TextDeltaEvent(request.SessionId, turnId, "ok");

        if (_blockingGate is { } gate)
        {
            _blockingGate = null;
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        yield return new TurnCompletedEvent(request.SessionId, turnId,
            new AgentTurnResult(turnId, request.SessionId, "ok", default, [], TimeSpan.Zero));
    }

    public void Dispose() => Pump.Dispose();
}
