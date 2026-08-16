using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Sessions;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>Default <see cref="IAgentRuntime"/>: session lifecycle + MAF agent execution + events.</summary>
/// <remarks>
/// Session ownership: the creator owns the session; other callers are rejected with <see cref="AgentErrorCode.SessionNotFound"/>
/// (404 rather than 403, so session ids cannot be probed — the attempt is logged, event 208) unless they carry the <c>"admin"</c>
/// role. The role name is hardcoded in 0.1 and becomes configurable in a later phase.
/// </remarks>
public sealed partial class ThalosAgentRuntime(
    IAgentCatalog agents,
    IAgentFactory agentFactory,
    IAgentSessionStore store,
    SessionStoreChatHistoryProvider historyProvider,
    IAgentNotificationPublisher publisher,
    AgentEventHub hub,
    TimeProvider clock,
    ILogger<ThalosAgentRuntime>? logger = null) : IAgentRuntime
{
    private const string AdminRole = "admin";
    private static readonly AgentTurnRequestValidator Validator = new(); // generated validator is stateless
    private readonly ILogger<ThalosAgentRuntime> _logger = logger ?? NullLogger<ThalosAgentRuntime>.Instance;

    // ---------- sessions ----------

    /// <inheritdoc />
    public async ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default)
    {
        if (!agents.TryGet(agentId, out _))
        {
            return Result<SessionId, AgentError>.Failure(AgentError.AgentNotFound(agentId));
        }

        var created = await store.CreateAsync(agentId, caller.Id, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result<SessionId, AgentError>.Failure(created.Error);
        }

        // the session exists in the store (source of truth); a failing notification must not report it as not created
        await PublishPostPersistAsync(new SessionCreatedNotification(created.Value.Id, agentId, caller.Id, clock.GetUtcNow()), created.Value.Id, ct).ConfigureAwait(false);
        LogSessionCreated(_logger, created.Value.Id, agentId, caller.Id);
        return Result<SessionId, AgentError>.Success(created.Value.Id);
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default)
    {
        var loaded = await LoadAuthorizedAsync(sessionId, caller, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return UnitResult<AgentError>.Failure(loaded.Error);
        }

        var machine = new AgentSessionMachine(loaded.Value.State);
        if (!machine.TryFire(SessionTrigger.Close))
        {
            return UnitResult<AgentError>.Failure(loaded.Value.State == SessionState.Closed
                ? AgentError.SessionClosed(sessionId)
                : AgentError.SessionBusy(sessionId));
        }

        // CAS from the state we validated against: if a turn claimed the session in between, the close loses and reports busy
        var closed = await store.TryTransitionAsync(sessionId, loaded.Value.State, machine.Current, ct).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            return UnitResult<AgentError>.Failure(closed.Error);
        }

        if (!closed.Value)
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionBusy(sessionId));
        }

        // the close is persisted; a failing notification must not report the session as still open
        await PublishPostPersistAsync(new SessionClosedNotification(sessionId, clock.GetUtcNow()), sessionId, ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    // ---------- buffered turn ----------

    /// <inheritdoc />
    /// <remarks>
    /// Folds <see cref="RunTurnStreamingAsync"/>: the result of the terminal <see cref="TurnCompletedEvent"/> or the error of
    /// the terminal <see cref="TurnFailedEvent"/>. Cancellation semantics are those of the streaming path: a token that is
    /// already cancelled before the session is claimed may throw <see cref="OperationCanceledException"/>; cancellation after
    /// the claim yields <see cref="AgentErrorCode.Cancelled"/> and the session returns to <see cref="SessionState.Idle"/>.
    /// </remarks>
    public async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
    {
        AgentTurnResult? result = null;
        AgentError? error = null;

        await foreach (var evt in RunTurnStreamingAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TurnCompletedEvent done: result = done.Result; break;
                case TurnFailedEvent failed: error = failed.Error; break;
            }
        }

        return result is not null
            ? Result<AgentTurnResult, AgentError>.Success(result)
            : Result<AgentTurnResult, AgentError>.Failure(error ?? AgentError.ProviderError("Turn produced no terminal event."));
    }

    // ---------- streaming turn (the real implementation; buffered delegates here) ----------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The MAF loop runs in a producer <see cref="Task"/> that owns the <see cref="TurnScope"/>; this iterator only
    /// drains the scope's event channel and fans events out to the <see cref="AgentEventHub"/>. An AsyncLocal scope
    /// would NOT survive <c>yield return</c> inside an async iterator (verified), so the scope must never live in the iterator.
    /// </para>
    /// <para>
    /// Pre-claim failures (validation, unknown or foreign session, unknown agent, busy or closed session) are returned to the
    /// caller as the single <see cref="TurnFailedEvent"/> and audited through <see cref="IAgentNotificationPublisher"/>, but are
    /// <em>not</em> fanned out to the hub: channel adapters filter hub events by session, and a stranger's rejected attempt must
    /// not surface in the owner's channel. Every event of a claimed turn — including its terminal event — reaches the hub.
    /// </para>
    /// <para>
    /// Cancellation: a token that is already cancelled before the session is claimed may surface as
    /// <see cref="OperationCanceledException"/> from the enumeration (thrown by whichever store call observes it first — no
    /// state has changed yet); once the session is claimed, cancellation always ends the stream with
    /// <see cref="TurnFailedEvent"/> (<see cref="AgentErrorCode.Cancelled"/>) and the session returns to <see cref="SessionState.Idle"/>.
    /// Disposing the enumerator mid-turn cancels the model loop and awaits its shutdown; a tool that ignores its token delays
    /// that shutdown (a bounded grace period is a follow-up).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = TurnId.New();
        var sessionId = request.SessionId;

        // 1. validate + load + authorize + claim (state machine) — no scope needed yet
        var start = await BeginTurnAsync(request, ct).ConfigureAwait(false);
        if (start.IsFailure)
        {
            // audited via the publisher, returned to the caller, deliberately NOT published to the hub (see remarks)
            yield return await FailAsync(sessionId, turnId, start.Error, usage: default, releaseState: false).ConfigureAwait(false);
            yield break;
        }

        // 2. the producer owns the scope (created here so it flows into the producer's async context) and writes every event.
        //    A linked CTS lets an abandoned enumeration (consumer stopped reading) cancel the model turn instead of leaking it.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var scope = TurnScope.Begin(sessionId, turnId, request.Caller);
        var producer = ProduceTurnAsync(scope, start.Value, request, producerCts.Token); // never throws; disposes the scope

        // 3. drain until the producer completes the channel; the reader ignores ct so a cancelled turn still ends with its error event
        try
        {
            await foreach (var evt in scope.Events.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await hub.PublishAsync(evt, CancellationToken.None).ConfigureAwait(false);
                yield return evt;
            }
        }
        finally
        {
            if (!producer.IsCompleted)
            {
                await producerCts.CancelAsync().ConfigureAwait(false); // consumer went away mid-turn
            }

            await producer.ConfigureAwait(false);

            // The consumer stopped reading before the producer finished: hub subscribers (channel adapters, audit) still get
            // the remaining events, including the terminal one — the abandoned enumeration cannot receive them any more.
            while (scope.Events.TryRead(out var rest))
            {
                await hub.PublishAsync(rest, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs the model loop for one turn on its own async flow. Owns and disposes <paramref name="scope"/>. Never throws.</summary>
    private async Task ProduceTurnAsync(TurnScope scope, AgentDefinition definition, AgentTurnRequest request, CancellationToken ct)
    {
        var sessionId = scope.SessionId;
        var turnId = scope.TurnId;
        using var activity = ThalosTelemetry.ActivitySource.StartActivity("thalos.turn");
        activity?.SetTag("thalos.agent", definition.Name).SetTag("thalos.session", sessionId.ToString()).SetTag("thalos.turn", turnId.ToString());
        var startedAt = Stopwatch.GetTimestamp();
        var text = new StringBuilder();
        // boxed so a round-trip that completed before the provider failed is still billed on the failed turn
        var usage = new StrongBox<TurnUsage>(TurnUsage.Empty(definition.Model ?? string.Empty));
        AgentError? failure = null;

        try
        {
            // the session is claimed; from here on every failure — including a throwing publisher — must release it
            await publisher.PublishAsync(new TurnStartedNotification(sessionId, turnId, definition.Id, request.Caller.Id, clock.GetUtcNow()), ct).ConfigureAwait(false);
            LogTurnStarted(_logger, turnId, sessionId, definition.Name);

            var agent = await agentFactory.GetOrCreateAsync(definition, ct).ConfigureAwait(false);
            if (agent.IsFailure)
            {
                failure = agent.Error;
            }
            else
            {
                await RunModelLoopAsync(scope, agent.Value, request.Text, text, usage, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = MapException(ex, ct);
            LogTurnException(_logger, turnId, ex.Message, ex); // the raw message stays in the log; AgentError.Detail carries only the type name
        }

        try
        {
            await FinishTurnAsync(scope, activity, failure, text.ToString(), usage.Value, Stopwatch.GetElapsedTime(startedAt)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // last resort: never let the producer fault silently — the drain would hang. Release the session best-effort,
            // then report the original failure if there was one, else what went wrong while finishing.
            await ReleaseBestEffortAsync(sessionId).ConfigureAwait(false);
            LogTurnException(_logger, turnId, ex.Message, ex);
            var error = failure ?? (ex is OperationCanceledException ? AgentError.Cancelled() : AgentError.StoreError("Failed to finish turn.", ex.GetType().Name));
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            ThalosTelemetry.TurnFailures.Add(1, new KeyValuePair<string, object?>("thalos.error", error.Code.ToString()));
            await PublishPostPersistAsync(new TurnFailedNotification(sessionId, turnId, error, clock.GetUtcNow(), usage.Value), sessionId, CancellationToken.None).ConfigureAwait(false);
            LogTurnFailed(_logger, turnId, sessionId, error.ToString());
            await scope.PublishAsync(new TurnFailedEvent(sessionId, turnId, error, usage.Value), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            scope.Dispose(); // completes the channel → the iterator's ReadAllAsync ends
        }
    }

    /// <summary>Second phase of a turn: records the outcome in the store, releases the session and publishes the terminal event(s). May throw (store).</summary>
    private async ValueTask FinishTurnAsync(TurnScope scope, Activity? activity, AgentError? failure, string text, TurnUsage usage, TimeSpan elapsed)
    {
        var sessionId = scope.SessionId;
        var turnId = scope.TurnId;
        if (failure is { } err)
        {
            activity?.SetStatus(ActivityStatusCode.Error, err.Message);
            await scope.PublishAsync(await FailAsync(sessionId, turnId, err, usage, releaseState: true).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var toolCalls = scope.ToolCalls.ToList();
        var completed = await CompleteTurnAsync(sessionId, turnId, text, usage, toolCalls, elapsed, CancellationToken.None).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, completed.Error.Message);
            await scope.PublishAsync(await FailAsync(sessionId, turnId, completed.Error, usage, releaseState: true).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        activity?.SetTag("thalos.tokens.input", usage.InputTokens).SetTag("thalos.tokens.output", usage.OutputTokens).SetTag("thalos.tool_calls", toolCalls.Count).SetStatus(ActivityStatusCode.Ok);
        await scope.PublishAsync(new UsageEvent(sessionId, turnId, usage), CancellationToken.None).ConfigureAwait(false);
        await scope.PublishAsync(new TurnCompletedEvent(sessionId, turnId, completed.Value), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams one MAF run: appends text deltas to <paramref name="text"/> (publishing each), sums <see cref="UsageContent"/>
    /// into <paramref name="usage"/> as it arrives (so a failure mid-turn keeps the completed round-trips' tokens) and adopts
    /// the provider-reported model id when the definition did not pin one.
    /// </summary>
    private async Task RunModelLoopAsync(TurnScope scope, AIAgent agent, string prompt, StringBuilder text, StrongBox<TurnUsage> usage, CancellationToken ct)
    {
        var mafSession = await historyProvider.CreateBoundSessionAsync(agent, scope.SessionId, ct).ConfigureAwait(false);
        await foreach (var update in agent.RunStreamingAsync(prompt, mafSession, cancellationToken: ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(usage.Value.ModelId) && update.AsChatResponseUpdate().ModelId is { Length: > 0 } modelId)
            {
                usage.Value = usage.Value with { ModelId = modelId };
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        text.Append(tc.Text);
                        await scope.PublishAsync(new TextDeltaEvent(scope.SessionId, scope.TurnId, tc.Text), CancellationToken.None).ConfigureAwait(false);
                        break;
                    case UsageContent uc:
                        usage.Value += new TurnUsage((int)(uc.Details.InputTokenCount ?? 0), (int)(uc.Details.OutputTokenCount ?? 0), usage.Value.ModelId);
                        break;
                }
            }
        }
    }

    // ---------- helpers ----------

    /// <summary>Validates, loads, authorizes and claims the session (Idle → Running). Nothing here may leave the session claimed on failure.</summary>
    private async ValueTask<Result<AgentDefinition, AgentError>> BeginTurnAsync(AgentTurnRequest request, CancellationToken ct)
    {
        var validation = Validator.Validate(request);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(request.Text))
        {
            return Result<AgentDefinition, AgentError>.Failure(AgentError.Validation("Text is required."));
        }

        var loaded = await LoadAuthorizedAsync(request.SessionId, request.Caller, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<AgentDefinition, AgentError>.Failure(loaded.Error);
        }

        var session = loaded.Value;
        if (!agents.TryGet(session.AgentId, out var definition))
        {
            return Result<AgentDefinition, AgentError>.Failure(AgentError.AgentNotFound(session.AgentId));
        }

        var machine = new AgentSessionMachine(session.State);
        if (!machine.TryFire(SessionTrigger.Start))
        {
            return Result<AgentDefinition, AgentError>.Failure(StartRejected(session.Id, session.State));
        }

        // Start is only valid from Idle, so this is the CAS Idle → Running; a concurrent claimant that got there first makes it return false
        var claimed = await store.TryTransitionAsync(session.Id, session.State, machine.Current, ct).ConfigureAwait(false);
        if (claimed.IsFailure)
        {
            return Result<AgentDefinition, AgentError>.Failure(claimed.Error);
        }

        if (claimed.Value)
        {
            return Result<AgentDefinition, AgentError>.Success(definition);
        }

        // lost the race: re-read to report the state that beat us (Closed → SessionClosed, anything else → SessionBusy)
        var current = await store.GetAsync(session.Id, ct).ConfigureAwait(false);
        return Result<AgentDefinition, AgentError>.Failure(current.IsFailure
            ? current.Error
            : StartRejected(session.Id, current.Value.State));
    }

    private static AgentError StartRejected(SessionId sessionId, SessionState state) =>
        state == SessionState.Closed ? AgentError.SessionClosed(sessionId) : AgentError.SessionBusy(sessionId);

    private async ValueTask<Result<AgentTurnResult, AgentError>> CompleteTurnAsync(SessionId sessionId, TurnId turnId, string text, TurnUsage usage, IReadOnlyList<ToolCallSummary> toolCalls, TimeSpan elapsed, CancellationToken ct)
    {
        var recorded = await store.RecordTurnAsync(sessionId, usage, ct).ConfigureAwait(false);
        if (recorded.IsFailure)
        {
            return Result<AgentTurnResult, AgentError>.Failure(recorded.Error);
        }

        var released = await store.UpdateStateAsync(sessionId, SessionState.Idle, ct).ConfigureAwait(false);
        if (released.IsFailure)
        {
            return Result<AgentTurnResult, AgentError>.Failure(released.Error);
        }

        ThalosTelemetry.Turns.Add(1);
        ThalosTelemetry.TurnDurationMs.Record(elapsed.TotalMilliseconds);
        ThalosTelemetry.InputTokens.Add(usage.InputTokens);
        ThalosTelemetry.OutputTokens.Add(usage.OutputTokens);

        // the turn is persisted and the session released; a failing notification must not report the turn as failed
        await PublishPostPersistAsync(new TurnCompletedNotification(sessionId, turnId, usage, elapsed, clock.GetUtcNow()), sessionId, ct).ConfigureAwait(false);
        LogTurnCompleted(_logger, turnId, sessionId, elapsed.TotalMilliseconds, usage.InputTokens, usage.OutputTokens);
        return Result<AgentTurnResult, AgentError>.Success(new AgentTurnResult(turnId, sessionId, text, usage, toolCalls, elapsed));
    }

    /// <summary>
    /// Records a failed turn: optionally releases the session (→ Idle), counts the failure, publishes <see cref="TurnFailedNotification"/>
    /// and returns the terminal event. <c>usage</c> is the token usage of the round-trips that completed before the failure — reported so
    /// hosts can bill failed/quarantined turns (default for pre-claim failures).
    /// </summary>
    private async ValueTask<TurnFailedEvent> FailAsync(SessionId sessionId, TurnId turnId, AgentError error, TurnUsage usage, bool releaseState)
    {
        if (releaseState)
        {
            // best effort; the turn is already failed
            var released = await store.UpdateStateAsync(sessionId, SessionState.Idle, CancellationToken.None).ConfigureAwait(false);
            if (released.IsFailure)
            {
                LogReleaseFailed(_logger, sessionId, released.Error.ToString());
            }
        }

        ThalosTelemetry.TurnFailures.Add(1, new KeyValuePair<string, object?>("thalos.error", error.Code.ToString()));
        // the outcome is decided (and, if claimed, the release persisted); a failing audit notification must not mask the real error
        await PublishPostPersistAsync(new TurnFailedNotification(sessionId, turnId, error, clock.GetUtcNow(), usage), sessionId, CancellationToken.None).ConfigureAwait(false);
        LogTurnFailed(_logger, turnId, sessionId, error.ToString());
        return new TurnFailedEvent(sessionId, turnId, error, usage);
    }

    /// <summary>
    /// Publishes a notification whose subject is already persisted (session created/closed, turn recorded/released). The store is
    /// the source of truth, so a failing publisher is logged and swallowed rather than reported to the caller as a failed operation.
    /// Only the caller's own cancellation (<paramref name="ct"/> signalled) propagates; a publisher-internal cancellation is a failure.
    /// </summary>
    private async ValueTask PublishPostPersistAsync<TNotification>(TNotification notification, SessionId sessionId, CancellationToken ct)
        where TNotification : ZeroAlloc.Mediator.INotification
    {
        try
        {
            await publisher.PublishAsync(notification, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogNotificationFailed(_logger, typeof(TNotification).Name, sessionId, ex.Message, ex);
        }
    }

    /// <summary>Releases the session (→ Idle) swallowing anything the store does; used only on the last-resort path where nothing else may throw.</summary>
    private async ValueTask ReleaseBestEffortAsync(SessionId sessionId)
    {
        try
        {
            var released = await store.UpdateStateAsync(sessionId, SessionState.Idle, CancellationToken.None).ConfigureAwait(false);
            if (released.IsFailure)
            {
                LogReleaseFailed(_logger, sessionId, released.Error.ToString());
            }
        }
        catch (Exception ex)
        {
            LogReleaseThrew(_logger, sessionId, ex.Message, ex);
        }
    }

    private async ValueTask<Result<AgentSessionRecord, AgentError>> LoadAuthorizedAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct)
    {
        var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var isOwner = string.Equals(loaded.Value.OwnerId, caller.Id, StringComparison.Ordinal);
        if (isOwner || caller.Roles.Contains(AdminRole))
        {
            return loaded;
        }

        // 404, not 403: a stranger must not learn that the session exists. The attempt is still audited here.
        LogForeignSessionAccess(_logger, caller.Id, sessionId);
        return Result<AgentSessionRecord, AgentError>.Failure(AgentError.SessionNotFound(sessionId));
    }

    /// <summary>
    /// Maps an exception from the model loop to an <see cref="AgentError"/>. Only a cancellation that stems from the turn's own
    /// token is <see cref="AgentErrorCode.Cancelled"/>; any other <see cref="OperationCanceledException"/> (provider HTTP timeout,
    /// tool-internal timeout) is the provider's failure. MAF/FunctionInvokingChatClient may wrap the cause one level deep.
    /// <see cref="AgentError.Detail"/> carries only the exception type name — provider/tool messages can echo untrusted content
    /// (prompt text, credential fragments) and are logged instead (event 207).
    /// </summary>
    private static AgentError MapException(Exception ex, CancellationToken ct) => ex switch
    {
        AgentTurnException ate => ate.Error,
        OperationCanceledException when ct.IsCancellationRequested => AgentError.Cancelled(),
        OperationCanceledException => AgentError.ProviderError("The model provider timed out or was cancelled internally.", ex.GetType().Name),
        { InnerException: AgentTurnException inner } => inner.Error,
        { InnerException: OperationCanceledException } when ct.IsCancellationRequested => AgentError.Cancelled(),
        { InnerException: OperationCanceledException inner } => AgentError.ProviderError("The model provider timed out or was cancelled internally.", inner.GetType().Name),
        _ => AgentError.ProviderError("The model provider failed.", ex.GetType().Name),
    };

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Session {SessionId} created for agent {AgentId} by {Caller}")]
    private static partial void LogSessionCreated(ILogger logger, SessionId sessionId, AgentId agentId, string caller);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Turn {TurnId} started on session {SessionId} (agent {Agent})")]
    private static partial void LogTurnStarted(ILogger logger, TurnId turnId, SessionId sessionId, string agent);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "Turn {TurnId} completed on session {SessionId} in {ElapsedMs}ms (in={InputTokens} out={OutputTokens})")]
    private static partial void LogTurnCompleted(ILogger logger, TurnId turnId, SessionId sessionId, double elapsedMs, int inputTokens, int outputTokens);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Turn {TurnId} failed on session {SessionId}: {Error}")]
    private static partial void LogTurnFailed(ILogger logger, TurnId turnId, SessionId sessionId, string error);

    [LoggerMessage(EventId = 204, Level = LogLevel.Warning, Message = "Failed to release session {SessionId} after a failed turn: {Error}")]
    private static partial void LogReleaseFailed(ILogger logger, SessionId sessionId, string error);

    [LoggerMessage(EventId = 205, Level = LogLevel.Warning, Message = "Releasing session {SessionId} after a failed turn threw: {Error}")]
    private static partial void LogReleaseThrew(ILogger logger, SessionId sessionId, string error, Exception exception);

    [LoggerMessage(EventId = 206, Level = LogLevel.Warning, Message = "Failed to publish {Notification} for session {SessionId}: {Error}")]
    private static partial void LogNotificationFailed(ILogger logger, string notification, SessionId sessionId, string error, Exception exception);

    [LoggerMessage(EventId = 207, Level = LogLevel.Warning, Message = "Turn {TurnId} provider/tool failure: {Error}")]
    private static partial void LogTurnException(ILogger logger, TurnId turnId, string error, Exception exception);

    [LoggerMessage(EventId = 208, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to use session {SessionId} it does not own; answered SessionNotFound")]
    private static partial void LogForeignSessionAccess(ILogger logger, string caller, SessionId sessionId);
}
