using Microsoft.Extensions.Logging;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>
/// Default <see cref="ISubagentRunner"/>: one detached turn on a fresh session, guarded and always closed.
/// </summary>
/// <remarks>
/// The session is closed in a <c>finally</c> rather than on the success path, because a session left Idle after a
/// failed run is indistinguishable from a live one and would hold its slot until the idle timeout. A close failure is
/// logged and swallowed: the run's own outcome is what the caller asked for, and losing a real result to report a
/// cleanup problem would be the wrong trade.
/// </remarks>
[Singleton(As = typeof(ISubagentRunner))]
public sealed partial class SubagentRunner : ISubagentRunner
{
    private readonly IAgentRuntime _runtime;
    private readonly SubagentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SubagentRunner>? _logger;

    /// <summary>Creates a runner bound to a single <paramref name="runtime"/>, ceiling set and clock.</summary>
    public SubagentRunner(IAgentRuntime runtime, SubagentOptions options, TimeProvider time, ILogger<SubagentRunner>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Both checks happen before any session is created: a session that will only ever be torn down for a bad
        // request (too deep, or a deadline that can't be honoured) should never occupy a slot in the first place.
        if (ValidateRequest(request, _options) is { } validationError)
        {
            return Result<AgentTurnResult, AgentError>.Failure(validationError);
        }

        var created = await _runtime.CreateSessionAsync(request.AgentId, request.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result<AgentTurnResult, AgentError>.Failure(created.Error);
        }

        var sessionId = created.Value;
        try
        {
            // Two independent sources can end the turn: the caller's own token, and a deadline measured over the
            // injected clock so tests can drive it without a real wall-clock wait. Linking them means either one
            // stops RunTurnAsync; which one fired is recovered afterwards from deadlineSource/ct, since a cancelled
            // linked token alone can't say which side tripped it.
            using var deadlineSource = new CancellationTokenSource(request.Budget.Deadline, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);

            var turn = await RunTurnAsync(request, sessionId, linked.Token).ConfigureAwait(false);

            // deadlineSource.IsCancellationRequested is true only once the budget's wall-clock ceiling has actually
            // elapsed; !ct.IsCancellationRequested rules out the case where the caller cancelled at (or after) the
            // same moment the deadline would have fired anyway. Without that guard, a caller cancelling right at the
            // deadline — the ordinary shape of a host shutdown — would be misreported as a runaway agent instead of
            // a routine cancellation.
            if (turn.IsFailure && deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Result<AgentTurnResult, AgentError>.Failure(
                    AgentError.SubagentDeadlineExceeded(request.Budget.Deadline));
            }

            return turn;
        }
        finally
        {
            // Every path through the try, success or failure, ends here: a detached run has no live caller to notice
            // and retry a stuck session, so this is the only place its slot gets freed. CancellationToken.None is
            // deliberate, not ct: a caller cancelling mid-turn is the normal reason a detached run fails, and
            // ThalosAgentRuntime.CloseSessionAsync short-circuits on an already-cancelled token before it closes
            // anything, which would leave the session Idle and indistinguishable from a live one until the idle
            // timeout. Cleanup has to outlive cancellation of the operation it is cleaning up after.
            var closed = await _runtime.CloseSessionAsync(sessionId, request.Caller, CancellationToken.None).ConfigureAwait(false);
            if (closed.IsFailure && _logger is not null)
            {
                LogCloseFailed(_logger, sessionId.ToString(), closed.Error.Code);
            }
        }
    }

    /// <summary>
    /// Guards on <paramref name="request"/> that must fail before a session is created: exceeding the configured
    /// nesting depth, and a deadline that isn't a positive <see cref="TimeSpan"/>. A non-positive deadline (zero,
    /// negative, or the <c>-1ms</c> "infinite" sentinel that <see cref="SubagentBudget"/> has no way to request
    /// deliberately) would otherwise reach <see cref="CancellationTokenSource"/> and throw
    /// <see cref="ArgumentOutOfRangeException"/>; this codebase returns <see cref="AgentError"/> instead of letting
    /// request data throw.
    /// </summary>
    private static AgentError? ValidateRequest(SubagentRunRequest request, SubagentOptions options)
    {
        if (request.Depth > options.MaxDepth)
        {
            return AgentError.SubagentDepthExceeded(request.Depth, options.MaxDepth);
        }

        if (request.Budget.Deadline <= TimeSpan.Zero)
        {
            return AgentError.Validation($"Subagent deadline must be positive; was {request.Budget.Deadline}.");
        }

        return null;
    }

    private async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(
        SubagentRunRequest request, SessionId sessionId, CancellationToken ct)
    {
        var turn = await _runtime
            .RunTurnAsync(new AgentTurnRequest(sessionId, request.Task, request.Caller), ct)
            .ConfigureAwait(false);

        // Post-hoc only: RunTurnAsync is buffered and returns after the whole turn has already run, so there is no
        // seam here to stop a turn mid-flight. This cannot halt a runaway turn already in progress — it converts an
        // overspend into a reported failure and bounds what a subsequent step is told it may spend. A cap that stops
        // a turn mid-flight would need to be pushed into the runtime's own round-trip loop; that is a larger change
        // and out of scope here.
        if (turn.IsSuccess && TotalTokens(turn.Value.Usage) > request.Budget.MaxTotalTokens)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentBudgetExceeded(request.Budget.MaxTotalTokens));
        }

        return turn;
    }

    // Cast the first operand so the addition itself happens in long, not int-then-widen: the long return type alone
    // does not stop the operands from overflowing as int before the result is ever assigned. Not reachable with real
    // token counts, but the previous form was misleading about where the widening actually occurred.
    private static long TotalTokens(TurnUsage usage) => (long)usage.InputTokens + usage.OutputTokens;

    [LoggerMessage(EventId = 800, Level = LogLevel.Warning,
        Message = "Closing detached session {SessionId} failed with {ErrorCode}; the run's own result is unaffected")]
    private static partial void LogCloseFailed(ILogger logger, string sessionId, AgentErrorCode errorCode);
}
