using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Thalos.Channels;

/// <summary>
/// Hosts every registered <see cref="IChannelSource"/>: reads inbound messages, binds them to agent sessions and
/// renders each turn back through the <see cref="IChannelAdapter"/> whose <c>ChannelId</c> matches. One reader loop
/// per source. A slash-command is handled inline on that loop (commands are fast, and the loop must stay
/// responsive), but an ordinary message starts its turn WITHOUT the loop waiting for it — the loop that is parked
/// inside a turn can never dequeue the next message, which would make <c>/cancel</c> and the busy notice
/// unreachable for exactly the conversation that needs them. A second ordinary message for a conversation that
/// already has a turn running gets <see cref="ChannelNotices.Busy"/> immediately rather than queuing behind it.
/// </summary>
public sealed partial class ChannelPump(
    IEnumerable<IChannelSource> sources,
    IEnumerable<IChannelAdapter> adapters,
    IAgentRuntime runtime,
    IAgentCatalog catalog,
    IConversationMap conversations,
    IOptions<ChannelOptions> options,
    TimeProvider clock,
    ILogger<ChannelPump> logger) : BackgroundService
{
    private readonly IReadOnlyList<IChannelSource> _sources = [.. sources];
    private readonly Dictionary<string, IChannelAdapter> _adapters =
        adapters.ToDictionary(a => a.ChannelId, StringComparer.Ordinal);

    private readonly IAgentRuntime _runtime = runtime;
    private readonly IAgentCatalog _catalog = catalog;
    private readonly IConversationMap _conversations = conversations;
    private readonly ChannelOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<ChannelPump> _logger = logger;

    /// <summary>
    /// The <see cref="CancellationTokenSource"/> backing the turn currently running for each (channel, conversation),
    /// so <c>/cancel</c> can abort it. Guarded by a lock rather than a concurrent dictionary: one source's reader loop
    /// handles its own messages strictly in order, but every source's loop shares this one instance, and
    /// <see cref="Dictionary{TKey,TValue}"/> is not safe for concurrent access even across disjoint keys.
    /// </summary>
    private readonly Dictionary<(string ChannelId, string ConversationId), CancellationTokenSource> _running = [];

    /// <summary>
    /// Every turn task currently in flight, so shutdown can drain them instead of abandoning them mid-write to a
    /// channel. Guarded by the same lock as <see cref="_running"/>; swept of already-completed entries whenever a
    /// new turn starts, since nothing else removes an individual entry once its turn finishes.
    /// </summary>
    private readonly List<Task> _turnTasks = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        await Task.WhenAll(_sources.Select(s => PumpAsync(s, stoppingToken))).ConfigureAwait(false);

        // Every reader loop just ended (shutdown, or every source failed). Any turn started before that has its
        // token linked to stoppingToken, so it is already unwinding on its own — wait for that rather than let
        // BackgroundService's stop abandon it mid-write to a channel or mid-call to the runtime. RunTrackedTurnAsync
        // never lets an exception escape, so this can never fault.
        await DrainRunningTurnsAsync().ConfigureAwait(false);
    }

    /// <summary>Awaits a snapshot of every still-in-flight turn task. Called once, after every reader loop has ended.</summary>
    private async Task DrainRunningTurnsAsync()
    {
        Task[] snapshot;
        lock (_running)
        {
            snapshot = [.. _turnTasks];
        }

        if (snapshot.Length > 0)
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(IChannelSource source, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(source.ChannelId, out var adapter))
        {
            LogNoAdapter(_logger, source.ChannelId);
            return;
        }

        try
        {
            await foreach (var message in source.ReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message, adapter, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message must not end the channel. A dead pump loop is invisible until process
                    // shutdown, and for a single-operator bot that reads as "the agent stopped answering".
                    LogHandleFailed(_logger, source.ChannelId, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            // The source itself died, so this loop is over — but siblings keep pumping. Never fail silently.
            LogSourceFailed(_logger, source.ChannelId, ex);
        }
    }

    /// <summary>
    /// Dispatches one inbound message: a recognised slash-command is handled here and never reaches the model — an
    /// unrecognised one is refused the same way, because forwarding a typo'd command as a prompt is the exact
    /// failure <see cref="ChannelCommand.Parse"/> exists to prevent. Anything else is an ordinary turn.
    /// </summary>
    private async Task HandleAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var command = ChannelCommand.Parse(message.Text);

        switch (command.Kind)
        {
            case ChannelCommandKind.Help:
                await NotifyAsync(adapter, SessionId.New(), ChannelNotices.Help, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Unknown:
                await NotifyAsync(adapter, SessionId.New(), ChannelNotices.UnknownCommand, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Cancel:
                await CancelRunningAsync(message, adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.New:
                await StartNewAsync(message, adapter, command.Argument, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.End:
                await EndAsync(message, adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Status:
                await StatusAsync(message, adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Agents:
                await AgentsAsync(adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.None:
            default:
                break;
        }

        await StartTurnAsync(message, adapter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an ordinary turn WITHOUT waiting for it to finish, so <see cref="PumpAsync"/>'s read loop returns to
    /// reading the next message immediately — a loop parked inside a turn can never dequeue <c>/cancel</c>, or a
    /// second message for the same conversation, for exactly the turn that needs interrupting. If this
    /// conversation already has a turn running, nothing new starts: <see cref="ChannelNotices.Busy"/> is reported
    /// right here. This is edge 4 firing at the pump level; the runtime-level <see cref="AgentErrorCode.SessionBusy"/>
    /// handling in <see cref="HandleLifecycleFailureAsync"/> stays in place as a backstop for a genuine cross-source
    /// race (two different reader loops racing to start a turn for the same conversation), but for the common case —
    /// one source, a second message while the first is still running — this check is what actually fires.
    /// </summary>
    private async Task StartTurnAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var key = (message.ChannelId, message.ConversationId.Value);
        CancellationTokenSource? turnCts = null;

        lock (_running)
        {
            if (!_running.ContainsKey(key))
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _running[key] = turnCts;
            }
        }

        if (turnCts is null)
        {
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.Busy, ct).ConfigureAwait(false);
            return;
        }

        var turnTask = RunTrackedTurnAsync(message, adapter, key, turnCts, ct);
        lock (_running)
        {
            _turnTasks.RemoveAll(t => t.IsCompleted);
            _turnTasks.Add(turnTask);
        }
    }

    /// <summary>
    /// Resolves and runs one turn under the token registered in <see cref="_running"/> so <c>/cancel</c> can abort
    /// it. This task is launched by <see cref="StartTurnAsync"/> and deliberately never awaited by the read loop, so
    /// no exception of any kind may escape it — an unobserved fault on a task nobody awaits is exactly the
    /// silent-death failure <see cref="PumpAsync"/>'s per-message catch exists to prevent for the loop itself; every
    /// path here ends in a log, a notice, or quiet completion, never an unhandled throw.
    /// </summary>
    private async Task RunTrackedTurnAsync(
        InboundMessage message, IChannelAdapter adapter, (string ChannelId, string ConversationId) key, CancellationTokenSource turnCts, CancellationToken ct)
    {
        try
        {
            var binding = await ResolveAsync(message, adapter, turnCts.Token).ConfigureAwait(false);
            if (binding is null)
            {
                return;
            }

            try
            {
                await RunTurnAsync(message, binding, adapter, turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // /cancel, not shutdown: cancelling turnCts alone (ct itself is untouched) is how the operator
                // aborts a running turn without tearing down the read loop that started it.
                await NotifyAsync(adapter, binding.SessionId, ChannelNotices.Cancelled, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled before a binding existed (during resolution/session creation) — nothing to route the
            // notice against yet, so use a fresh id the same way ResolveAsync's own failure notices do.
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.Cancelled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Real pump shutdown (ct itself is cancelled) — end quietly, same as the read loop's own shutdown path.
        }
        catch (Exception ex)
        {
            LogHandleFailed(_logger, message.ChannelId, ex);
        }
        finally
        {
            turnCts.Dispose();
            lock (_running)
            {
                _running.Remove(key);
            }
        }
    }

    /// <summary>
    /// Starts a fresh session, optionally against a named agent. Resolution happens before anything else is
    /// touched: an unknown name must refuse and leave the current binding exactly as it was, because a typo would
    /// otherwise silently destroy a live session the operator was still using.
    /// </summary>
    private async Task StartNewAsync(InboundMessage message, IChannelAdapter adapter, string? agentName, CancellationToken ct)
    {
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        var current = existing.IsSuccess ? existing.Value : null;

        if (ResolveAgent(agentName ?? _options.DefaultAgent) is not { } definition)
        {
            await NotifyAsync(adapter, current?.SessionId ?? SessionId.New(), ChannelNotices.UnknownAgent, ct).ConfigureAwait(false);
            return;
        }

        if (current is not null)
        {
            var closed = await _runtime.CloseSessionAsync(current.SessionId, message.Caller, ct).ConfigureAwait(false);
            if (closed.IsFailure)
            {
                LogCloseSessionFailed(_logger, message.ChannelId, closed.Error.Code);
            }
        }

        await CreateAndBindAsync(message, adapter, definition.Id, $"Started a new session with {definition.Name}.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes and unbinds the current session. Unbinds even when <see cref="IAgentRuntime.CloseSessionAsync"/>
    /// fails: <see cref="AgentErrorCode.SessionNotFound"/> or <see cref="AgentErrorCode.SessionClosed"/> mean the
    /// runtime already agrees there is nothing left to close, and <see cref="AgentErrorCode.SessionBusy"/> means a
    /// turn is still finishing on a session the operator explicitly asked to leave — refusing to unbind in either
    /// case would strand the operator with a session they cannot get back into except by waiting out the idle
    /// timeout, which is a worse outcome than an orphaned session finishing quietly in the background.
    /// </summary>
    private async Task EndAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        var binding = existing.IsSuccess ? existing.Value : null;

        if (binding is null)
        {
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.NoSession, ct).ConfigureAwait(false);
            return;
        }

        var closed = await _runtime.CloseSessionAsync(binding.SessionId, message.Caller, ct).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            LogCloseSessionFailed(_logger, message.ChannelId, closed.Error.Code);
        }

        await _conversations.UnbindAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        await NotifyAsync(adapter, binding.SessionId, ChannelNotices.SessionEnded, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the bound session without creating one: an unbound conversation gets <see cref="ChannelNotices.NoSession"/>
    /// and the map is left untouched, because a status check that has the side effect of starting a session would be
    /// a trap for an operator just checking in. The agent is reported by <see cref="AgentDefinition.Name"/>, never
    /// by <see cref="AgentId"/> — a ULID means nothing to a human reading it in a chat.
    /// </summary>
    private async Task StatusAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        var binding = existing.IsSuccess ? existing.Value : null;

        if (binding is null)
        {
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.NoSession, ct).ConfigureAwait(false);
            return;
        }

        var agentName = _catalog.TryGet(binding.AgentId, out var definition) ? definition.Name : "an unknown agent";
        await NotifyAsync(adapter, binding.SessionId, $"Bound to {agentName}. Last activity {binding.LastActivityAt:u}.", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the registered agents by <see cref="AgentDefinition.Name"/> (with <see cref="AgentDefinition.Description"/>
    /// when set) — never by <see cref="AgentId"/>, which renders as a 26-character ULID that is useless to a human.
    /// </summary>
    private async Task AgentsAsync(IChannelAdapter adapter, CancellationToken ct)
    {
        var lines = _catalog.Agents.Select(a =>
            string.IsNullOrEmpty(a.Description) ? a.Name : $"{a.Name} — {a.Description}");
        var text = "Available agents:\n" + string.Join('\n', lines);

        await NotifyAsync(adapter, SessionId.New(), text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the turn running for this conversation, or reports there is none. Touches only <see cref="_running"/>.
    /// The lookup and the cancel are two separate steps, so the turn can finish — and <see cref="RunTrackedTurnAsync"/>
    /// can remove and dispose its token — in the gap between them; that race is reported the same as "nothing is
    /// running" rather than surfacing an <see cref="ObjectDisposedException"/> to the operator as an error.
    /// </summary>
    private async Task CancelRunningAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var key = (message.ChannelId, message.ConversationId.Value);
        CancellationTokenSource? running;
        lock (_running)
        {
            _running.TryGetValue(key, out running);
        }

        var nothingToCancel = running is null;
        if (running is not null)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                nothingToCancel = true;
            }
        }

        if (nothingToCancel)
        {
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.NothingToCancel, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the session a message runs against, covering the four lifecycle edges: implicit bind (nothing bound
    /// yet), idle rollover (bound, but stale), and — handled here only by falling through to a fresh bind — the two
    /// cases <see cref="RunTurnAsync"/> reports after a turn actually fails: dead-session rebind and busy rejection.
    /// </summary>
    private async Task<ConversationBinding?> ResolveAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        var bound = existing.IsSuccess ? existing.Value : null;

        if (bound is not null && _clock.GetUtcNow() - bound.LastActivityAt <= _options.IdleTimeout)
        {
            return bound;
        }

        // bound is null: nothing was ever bound, so this is the implicit first-message bind — no notice needed.
        // bound is non-null: the binding is stale, so this is an idle rollover — the operator must be told, because
        // silent rollover makes the agent look amnesiac after every long gap.
        var notice = bound is null ? null : ChannelNotices.IdleRollover;
        if (ResolveAgent(_options.DefaultAgent) is not { } definition)
        {
            LogUnknownAgent(_logger, _options.DefaultAgent);
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.UnknownDefaultAgent, ct).ConfigureAwait(false);
            return null;
        }

        return await CreateAndBindAsync(message, adapter, definition.Id, notice, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a fresh session, binds it, and — when <paramref name="notice"/> is set — tells the operator why.</summary>
    private async Task<ConversationBinding?> CreateAndBindAsync(
        InboundMessage message, IChannelAdapter adapter, AgentId agentId, string? notice, CancellationToken ct)
    {
        var created = await _runtime.CreateSessionAsync(agentId, message.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            LogSessionFailed(_logger, message.ChannelId, created.Error.Code);
            return null;
        }

        var binding = new ConversationBinding(
            message.ChannelId, message.ConversationId, created.Value, agentId, _clock.GetUtcNow());

        await _conversations.BindAsync(binding, ct).ConfigureAwait(false);

        if (notice is not null)
        {
            await NotifyAsync(adapter, binding.SessionId, notice, ct).ConfigureAwait(false);
        }

        return binding;
    }

    /// <summary>Delivers a plain operator notice (not model output) through the adapter as a synthetic text delta.</summary>
    private static async Task NotifyAsync(IChannelAdapter adapter, SessionId sessionId, string text, CancellationToken ct) =>
        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, TurnId.New(), text), ct).ConfigureAwait(false);

    /// <summary>
    /// Resolves a configured or operator-typed agent NAME to its definition. <c>AgentId</c> is a ULID, so configuration
    /// and chat commands name agents by <see cref="AgentDefinition.Name"/>; the catalogue's own TryGet only indexes by id.
    /// Case-insensitive because the name is typed by a human, often on a phone.
    /// </summary>
    private AgentDefinition? ResolveAgent(string name)
    {
        foreach (var definition in _catalog.Agents)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private async Task RunTurnAsync(InboundMessage message, ConversationBinding binding, IChannelAdapter adapter, CancellationToken ct)
    {
        var coalescer = new DeltaCoalescer(_options.FlushInterval, _clock);
        var request = new AgentTurnRequest(binding.SessionId, message.Text, message.Caller);

        // Set when the dead-session case below clears the binding, so the trailing refresh does not recreate it.
        var unbound = false;

        await foreach (var agentEvent in _runtime.RunTurnStreamingAsync(request, ct).ConfigureAwait(false))
        {
            if (agentEvent is TurnFailedEvent failed &&
                failed.Error.Code is AgentErrorCode.SessionBusy or AgentErrorCode.SessionNotFound or AgentErrorCode.SessionClosed)
            {
                unbound = await HandleLifecycleFailureAsync(message, binding, adapter, failed, ct).ConfigureAwait(false);
                continue;
            }

            await DeliverEventAsync(coalescer, binding, adapter, agentEvent, ct).ConfigureAwait(false);
        }

        if (!unbound)
        {
            // Only touch LastActivityAt on a binding that is still current — re-writing it here after the
            // SessionNotFound/SessionClosed case above would silently recreate the binding we just cleared.
            await _conversations.BindAsync(binding with { LastActivityAt = _clock.GetUtcNow() }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Coalesces and forwards one streamed event that is not a lifecycle-ending turn failure.</summary>
    private static async Task DeliverEventAsync(
        DeltaCoalescer coalescer, ConversationBinding binding, IChannelAdapter adapter, AgentEvent agentEvent, CancellationToken ct)
    {
        switch (agentEvent)
        {
            case TextDeltaEvent delta:
                if (coalescer.TryAppend(delta.Text, out var render) && render is not null)
                {
                    await adapter.DeliverAsync(binding.SessionId,
                        new TextDeltaEvent(delta.SessionId, delta.TurnId, render), ct).ConfigureAwait(false);
                }

                break;

            case ToolCallStartedEvent started:
                coalescer.SetActivity(started.ToolName);
                if (coalescer.TryAppend(string.Empty, out var toolRender) && toolRender is not null)
                {
                    await adapter.DeliverAsync(binding.SessionId,
                        new TextDeltaEvent(started.SessionId, started.TurnId, toolRender), ct).ConfigureAwait(false);
                }

                break;

            case ToolCallFinishedEvent:
                coalescer.SetActivity(null);
                break;

            default:
                // Terminal and informational events pass through untouched; the adapter decides how to show them.
                await adapter.DeliverAsync(binding.SessionId, agentEvent, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Handles a turn failure that means "the binding was wrong" rather than "the turn failed": a busy session is
    /// reported so the operator can <c>/cancel</c> it (not queued), and a dead session (the runtime no longer has it)
    /// is unbound and the operator is asked to resend — deliberately not auto-retried, since retrying against a
    /// runtime that just rejected the session is how a rebind loop starts. Returns true when the binding was cleared.
    /// </summary>
    private async Task<bool> HandleLifecycleFailureAsync(
        InboundMessage message, ConversationBinding binding, IChannelAdapter adapter, TurnFailedEvent failed, CancellationToken ct)
    {
        if (failed.Error.Code == AgentErrorCode.SessionBusy)
        {
            await NotifyAsync(adapter, binding.SessionId, ChannelNotices.Busy, ct).ConfigureAwait(false);
            return false;
        }

        await _conversations.UnbindAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        await NotifyAsync(adapter, binding.SessionId, ChannelNotices.Rebound, ct).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Channels are disabled; the pump is idle")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 602, Level = LogLevel.Error, Message = "Channel {ChannelId} has a source but no adapter; it will not be pumped")]
    private static partial void LogNoAdapter(ILogger logger, string channelId);

    [LoggerMessage(EventId = 603, Level = LogLevel.Error, Message = "Channel {ChannelId} could not create a session: {Code}")]
    private static partial void LogSessionFailed(ILogger logger, string channelId, AgentErrorCode code);

    [LoggerMessage(EventId = 604, Level = LogLevel.Error, Message = "No agent is registered under the name {Name}; check Thalos:Channels:DefaultAgent against the agent catalogue")]
    private static partial void LogUnknownAgent(ILogger logger, string name);

    [LoggerMessage(EventId = 605, Level = LogLevel.Error, Message = "Channel {ChannelId} failed handling a message; the channel keeps reading")]
    private static partial void LogHandleFailed(ILogger logger, string channelId, Exception ex);

    [LoggerMessage(EventId = 606, Level = LogLevel.Critical, Message = "Channel {ChannelId} source loop ended with an error; that channel is no longer reading")]
    private static partial void LogSourceFailed(ILogger logger, string channelId, Exception ex);

    [LoggerMessage(EventId = 607, Level = LogLevel.Warning, Message = "Channel {ChannelId} asked the runtime to close a session and it refused: {Code}; the local binding is cleared regardless")]
    private static partial void LogCloseSessionFailed(ILogger logger, string channelId, AgentErrorCode code);
}
