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

        // A request-supplied budget always wins over the host default; see SubagentRunRequest.Budget and
        // SubagentOptions.DefaultBudget for the same rule stated from each side.
        var budget = request.Budget ?? _options.DefaultBudget;

        // Both checks happen before any session is created: a session that will only ever be torn down for a bad
        // request (too deep, or a deadline/token cap that can't be honoured) should never occupy a slot in the
        // first place.
        if (ValidateRequest(request, _options, budget) is { } validationError)
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
            using var deadlineSource = new CancellationTokenSource(budget.Deadline, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);

            var turn = await RunTurnAsync(request, sessionId, budget, linked.Token).ConfigureAwait(false);

            // deadlineSource.IsCancellationRequested is true only once the budget's wall-clock ceiling has actually
            // elapsed; !ct.IsCancellationRequested rules out the case where the caller cancelled at (or after) the
            // same moment the deadline would have fired anyway. Without that guard, a caller cancelling right at the
            // deadline — the ordinary shape of a host shutdown — would be misreported as a runaway agent instead of
            // a routine cancellation.
            var deadlinePassed = deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested;

            return ReconcileDeadline(turn, deadlinePassed, budget, sessionId, request.ParentSessionId);
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
                LogCloseFailed(_logger, sessionId.ToString(), request.ParentSessionId?.ToString(), closed.Error.Code);
            }
        }
    }

    /// <summary>
    /// Settles what the deadline guard is and is not allowed to do to <paramref name="turn"/>'s verdict, and logs the
    /// one case that would otherwise be invisible. Split out of <see cref="RunAsync"/> to keep that method's guard
    /// logic separate from the linked-token setup and session lifecycle around it.
    /// </summary>
    /// <remarks>
    /// <paramref name="turn"/>.Error.Code == <see cref="AgentErrorCode.Cancelled"/>, not merely <c>turn.IsFailure</c>,
    /// is what may be relabelled here. <see cref="RunTurnAsync"/> can itself fail with
    /// <see cref="AgentErrorCode.SubagentBudgetExceeded"/> once the turn has already completed, and the runtime can
    /// fail with a genuine <see cref="AgentErrorCode.ProviderError"/> that happens to land after the deadline
    /// elapsed; both are settled verdicts the runner produced or received on their own merits, and blindly
    /// overwriting whichever failure happens to be sitting here because the wall clock also elapsed would discard
    /// the real reason the run failed (this used to be exactly that bug: a turn that finished over budget <em>and</em>
    /// over deadline reported <see cref="AgentErrorCode.SubagentDeadlineExceeded"/>, silently losing the budget
    /// verdict). <see cref="AgentErrorCode.Cancelled"/> is the one code that carries no more specific reason than
    /// "the linked token fired" — that is the only shape this guard is entitled to reinterpret as a deadline breach.
    /// <para>
    /// A deadline is a stop signal, not a verdict on the answer: a turn that raced past the wall-clock ceiling and
    /// still came back with a real result already spent the tokens it spent, and returning that result as a success
    /// is correct — see <see cref="ISubagentRunner"/>'s remarks. But "succeeded, arrived late" is otherwise
    /// invisible: nothing about a successful <see cref="Result{TValue, TError}"/> says it blew through its own
    /// deadline. Logging it here is the only place that observation can be made.
    /// </para>
    /// </remarks>
    private Result<AgentTurnResult, AgentError> ReconcileDeadline(
        Result<AgentTurnResult, AgentError> turn, bool deadlinePassed, SubagentBudget budget, SessionId sessionId, SessionId? parentSessionId)
    {
        if (turn.IsFailure && turn.Error.Code == AgentErrorCode.Cancelled && deadlinePassed)
        {
            return Result<AgentTurnResult, AgentError>.Failure(AgentError.SubagentDeadlineExceeded(budget.Deadline));
        }

        if (turn.IsSuccess && deadlinePassed && _logger is not null)
        {
            LogSucceededAfterDeadline(_logger, sessionId.ToString(), parentSessionId?.ToString(), budget.Deadline);
        }

        return turn;
    }

    /// <summary>
    /// The largest delay <see cref="CancellationTokenSource"/> accepts: <c>uint.MaxValue - 1</c> milliseconds
    /// (~49.7 days), one below the <c>uint.MaxValue</c> value its underlying timer reserves as an "infinite" sentinel.
    /// Anything above this throws <see cref="ArgumentOutOfRangeException"/> from the constructor used in
    /// <see cref="RunAsync"/> — confirmed empirically against both <c>net8.0</c> and <c>net10.0</c>, since the BCL
    /// does not name the ceiling in its own documentation.
    /// </summary>
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Guards on <paramref name="request"/> and its resolved <paramref name="budget"/> that must fail before a
    /// session is created: exceeding the configured nesting depth, a deadline that isn't a positive
    /// <see cref="TimeSpan"/>, a deadline above <see cref="MaxDeadline"/>, and a non-positive token cap. A
    /// non-positive deadline (zero, negative, or the <c>-1ms</c> "infinite" sentinel that <see cref="SubagentBudget"/>
    /// has no way to request deliberately) and a deadline above <see cref="MaxDeadline"/> would otherwise both reach
    /// <see cref="CancellationTokenSource"/> and throw <see cref="ArgumentOutOfRangeException"/> — a caller reaching
    /// for "effectively no deadline" via <see cref="TimeSpan.MaxValue"/> would hit exactly that, from an API whose
    /// own XML doc promises an <see cref="AgentError"/> rather than a throw on request data. A non-positive token cap
    /// would otherwise create the session and run the turn before failing after the fact — spending exactly the
    /// money the guard exists to prevent.
    /// </summary>
    private static AgentError? ValidateRequest(SubagentRunRequest request, SubagentOptions options, SubagentBudget budget)
    {
        if (request.Depth > options.MaxDepth)
        {
            return AgentError.SubagentDepthExceeded(request.Depth, options.MaxDepth);
        }

        if (budget.Deadline <= TimeSpan.Zero)
        {
            return AgentError.Validation($"Subagent deadline must be positive; was {budget.Deadline}.");
        }

        if (budget.Deadline > MaxDeadline)
        {
            return AgentError.Validation(
                $"Subagent deadline must not exceed {MaxDeadline} (the CancellationTokenSource ceiling); was {budget.Deadline}.");
        }

        if (budget.MaxTotalTokens <= 0)
        {
            return AgentError.Validation($"Subagent token budget must be positive; was {budget.MaxTotalTokens}.");
        }

        return null;
    }

    private async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(
        SubagentRunRequest request, SessionId sessionId, SubagentBudget budget, CancellationToken ct)
    {
        // RequiredOutcome travels with the turn, not with the session: the runtime offers the outcome tool for this
        // one turn only. Forwarding it here is the whole of what makes SubagentRunRequest.RequiredOutcome do
        // anything - drop this line and the tool is never offered, the model never calls it, and every constrained
        // node fails as "completed without reporting an outcome".
        var turn = await _runtime
            .RunTurnAsync(
                new AgentTurnRequest(sessionId, request.Task, request.Caller) { RequiredOutcome = request.RequiredOutcome },
                ct)
            .ConfigureAwait(false);

        // Post-hoc only: RunTurnAsync is buffered and returns after the whole turn has already run, so there is no
        // seam here to stop a turn mid-flight. This cannot halt a runaway turn already in progress — it converts an
        // overspend into a reported failure and bounds what a subsequent step is told it may spend. A cap that stops
        // a turn mid-flight would need to be pushed into the runtime's own round-trip loop; that is a larger change
        // and out of scope here.
        if (turn.IsSuccess && TotalTokens(turn.Value.Usage) > budget.MaxTotalTokens)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentBudgetExceeded(budget.MaxTotalTokens));
        }

        return turn;
    }

    // Cast the first operand so the addition itself happens in long, not int-then-widen: the long return type alone
    // does not stop the operands from overflowing as int before the result is ever assigned. Not reachable with real
    // token counts, but the previous form was misleading about where the widening actually occurred.
    private static long TotalTokens(TurnUsage usage) => (long)usage.InputTokens + usage.OutputTokens;

    [LoggerMessage(EventId = 800, Level = LogLevel.Warning,
        Message = "Closing detached session {SessionId} (parent {ParentSessionId}) failed with {ErrorCode}; the run's own result is unaffected")]
    private static partial void LogCloseFailed(ILogger logger, string sessionId, string? parentSessionId, AgentErrorCode errorCode);

    [LoggerMessage(EventId = 801, Level = LogLevel.Warning,
        Message = "Detached session {SessionId} (parent {ParentSessionId}) succeeded after its deadline of {Deadline} had already elapsed")]
    private static partial void LogSucceededAfterDeadline(ILogger logger, string sessionId, string? parentSessionId, TimeSpan deadline);
}
