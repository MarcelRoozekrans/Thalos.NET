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
public sealed partial class ThalosAgentRuntime(
    IAgentCatalog agents,
    IAgentFactory agentFactory,
    IAgentSessionStore store,
    SessionStoreChatHistoryProvider historyProvider,
    IAgentNotificationPublisher publisher,
    AgentEventHub hub,
    TimeProvider clock,
    ILogger<ThalosAgentRuntime>? logger) : IAgentRuntime
{
    private const string AdminRole = "admin";
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

        await publisher.PublishAsync(new SessionCreatedNotification(created.Value.Id, agentId, caller.Id, clock.GetUtcNow()), ct).ConfigureAwait(false);
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

        var updated = await store.UpdateStateAsync(sessionId, machine.Current, ct).ConfigureAwait(false);
        if (updated.IsFailure)
        {
            return updated;
        }

        await publisher.PublishAsync(new SessionClosedNotification(sessionId, clock.GetUtcNow()), ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    // ---------- buffered turn ----------

    /// <inheritdoc />
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
    /// The MAF loop runs in a producer <see cref="Task"/> that owns the <see cref="TurnScope"/>; this iterator only
    /// drains the scope's event channel and fans events out to the <see cref="AgentEventHub"/>. An AsyncLocal scope
    /// would NOT survive <c>yield return</c> inside an async iterator (verified), so the scope must never live in the iterator.
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = TurnId.New();
        var sessionId = request.SessionId;

        // 1. validate + load + authorize + claim (state machine) — no scope needed yet
        var start = await BeginTurnAsync(request, turnId, ct).ConfigureAwait(false);
        if (start.IsFailure)
        {
            var failed = await FailAsync(sessionId, turnId, start.Error, releaseState: false).ConfigureAwait(false);
            await hub.PublishAsync(failed, CancellationToken.None).ConfigureAwait(false);
            yield return failed;
            yield break;
        }

        // 2. the producer owns the scope (created here so it flows into the producer's async context) and writes every event.
        //    A linked CTS lets an abandoned enumeration (consumer stopped reading) cancel the model turn instead of leaking it.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var scope = TurnScope.Begin(sessionId, turnId, request.Caller);
        var producer = ProduceTurnAsync(scope, start.Value.Definition, request, producerCts.Token); // never throws; disposes the scope

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
        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();
        var usage = TurnUsage.Empty(definition.Model ?? string.Empty);
        AgentError? failure = null;

        try
        {
            var agent = await agentFactory.GetOrCreateAsync(definition, ct).ConfigureAwait(false);
            if (agent.IsFailure)
            {
                failure = agent.Error;
            }
            else
            {
                usage = await RunModelLoopAsync(scope, agent.Value, request.Text, text, usage, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = MapException(ex);
        }

        sw.Stop();
        try
        {
            if (failure is { } err)
            {
                activity?.SetStatus(ActivityStatusCode.Error, err.Message);
                await scope.PublishAsync(await FailAsync(sessionId, turnId, err, releaseState: true).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var completed = await CompleteTurnAsync(sessionId, turnId, text.ToString(), usage, scope.ToolCalls.ToList(), sw.Elapsed, CancellationToken.None).ConfigureAwait(false);
            if (completed.IsFailure)
            {
                await scope.PublishAsync(await FailAsync(sessionId, turnId, completed.Error, releaseState: true).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await scope.PublishAsync(new UsageEvent(sessionId, turnId, usage), CancellationToken.None).ConfigureAwait(false);
            await scope.PublishAsync(new TurnCompletedEvent(sessionId, turnId, completed.Value), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // last resort: never let the producer fault silently — the drain would hang (OCE here → Cancelled)
            await scope.PublishAsync(new TurnFailedEvent(sessionId, turnId, ex is OperationCanceledException ? AgentError.Cancelled() : AgentError.StoreError("Failed to finish turn.", ex.Message)), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            scope.Dispose(); // completes the channel → the iterator's ReadAllAsync ends
        }
    }

    /// <summary>Streams one MAF run: appends text deltas to <paramref name="text"/> (publishing each), sums <see cref="UsageContent"/> into the returned usage.</summary>
    private async Task<TurnUsage> RunModelLoopAsync(TurnScope scope, AIAgent agent, string prompt, StringBuilder text, TurnUsage usage, CancellationToken ct)
    {
        var mafSession = await historyProvider.CreateBoundSessionAsync(agent, scope.SessionId, ct).ConfigureAwait(false);
        await foreach (var update in agent.RunStreamingAsync(prompt, mafSession, cancellationToken: ct).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        text.Append(tc.Text);
                        await scope.PublishAsync(new TextDeltaEvent(scope.SessionId, scope.TurnId, tc.Text), CancellationToken.None).ConfigureAwait(false);
                        break;
                    case UsageContent uc:
                        usage += new TurnUsage((int)(uc.Details.InputTokenCount ?? 0), (int)(uc.Details.OutputTokenCount ?? 0), usage.ModelId);
                        break;
                }
            }
        }

        return usage;
    }

    // ---------- helpers ----------

    private async ValueTask<Result<(AgentDefinition Definition, AgentSessionRecord Session), AgentError>> BeginTurnAsync(AgentTurnRequest request, TurnId turnId, CancellationToken ct)
    {
        var validation = new AgentTurnRequestValidator().Validate(request);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(request.Text))
        {
            return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Failure(AgentError.Validation("Text is required."));
        }

        var loaded = await LoadAuthorizedAsync(request.SessionId, request.Caller, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Failure(loaded.Error);
        }

        var session = loaded.Value;
        if (!agents.TryGet(session.AgentId, out var definition))
        {
            return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Failure(AgentError.AgentNotFound(session.AgentId));
        }

        var machine = new AgentSessionMachine(session.State);
        if (!machine.TryFire(SessionTrigger.Start))
        {
            return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Failure(session.State == SessionState.Closed
                ? AgentError.SessionClosed(session.Id)
                : AgentError.SessionBusy(session.Id));
        }

        var claimed = await store.UpdateStateAsync(session.Id, machine.Current, ct).ConfigureAwait(false);
        if (claimed.IsFailure)
        {
            return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Failure(claimed.Error);
        }

        await publisher.PublishAsync(new TurnStartedNotification(session.Id, turnId, definition.Id, request.Caller.Id, clock.GetUtcNow()), ct).ConfigureAwait(false);
        LogTurnStarted(_logger, turnId, session.Id, definition.Name);
        return Result<(AgentDefinition, AgentSessionRecord), AgentError>.Success((definition, session));
    }

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

        await publisher.PublishAsync(new TurnCompletedNotification(sessionId, turnId, usage, elapsed, clock.GetUtcNow()), ct).ConfigureAwait(false);
        LogTurnCompleted(_logger, turnId, sessionId, elapsed.TotalMilliseconds, usage.InputTokens, usage.OutputTokens);
        return Result<AgentTurnResult, AgentError>.Success(new AgentTurnResult(turnId, sessionId, text, usage, toolCalls, elapsed));
    }

    private async ValueTask<TurnFailedEvent> FailAsync(SessionId sessionId, TurnId turnId, AgentError error, bool releaseState)
    {
        if (releaseState)
        {
            // best effort; the turn is already failed
            await store.UpdateStateAsync(sessionId, SessionState.Idle, CancellationToken.None).ConfigureAwait(false);
        }

        ThalosTelemetry.TurnFailures.Add(1, new KeyValuePair<string, object?>("thalos.error", error.Code.ToString()));
        await publisher.PublishAsync(new TurnFailedNotification(sessionId, turnId, error, clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        LogTurnFailed(_logger, turnId, sessionId, error.ToString());
        return new TurnFailedEvent(sessionId, turnId, error);
    }

    private async ValueTask<Result<AgentSessionRecord, AgentError>> LoadAuthorizedAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct)
    {
        var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var isOwner = string.Equals(loaded.Value.OwnerId, caller.Id, StringComparison.Ordinal);
        return isOwner || caller.Roles.Contains(AdminRole)
            ? loaded
            : Result<AgentSessionRecord, AgentError>.Failure(AgentError.Unauthorized($"Caller '{caller.Id}' does not own session '{sessionId}'."));
    }

    private static AgentError MapException(Exception ex) => ex switch
    {
        AgentTurnException ate => ate.Error,
        OperationCanceledException => AgentError.Cancelled(),
        // FunctionInvokingChatClient / MAF wrap inner exceptions; unwrap one level for a useful code
        { InnerException: AgentTurnException inner } => inner.Error,
        { InnerException: OperationCanceledException } => AgentError.Cancelled(),
        _ => AgentError.ProviderError("The model provider failed.", ex.Message),
    };

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Session {SessionId} created for agent {AgentId} by {Caller}")]
    private static partial void LogSessionCreated(ILogger logger, SessionId sessionId, AgentId agentId, string caller);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Turn {TurnId} started on session {SessionId} (agent {Agent})")]
    private static partial void LogTurnStarted(ILogger logger, TurnId turnId, SessionId sessionId, string agent);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "Turn {TurnId} completed on session {SessionId} in {ElapsedMs}ms (in={InputTokens} out={OutputTokens})")]
    private static partial void LogTurnCompleted(ILogger logger, TurnId turnId, SessionId sessionId, double elapsedMs, int inputTokens, int outputTokens);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Turn {TurnId} failed on session {SessionId}: {Error}")]
    private static partial void LogTurnFailed(ILogger logger, TurnId turnId, SessionId sessionId, string error);
}
