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

        // Checked before any session is created: a session that will only ever be torn down for exceeding depth
        // should never occupy a slot in the first place.
        if (request.Depth > _options.MaxDepth)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentDepthExceeded(request.Depth, _options.MaxDepth));
        }

        var created = await _runtime.CreateSessionAsync(request.AgentId, request.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result<AgentTurnResult, AgentError>.Failure(created.Error);
        }

        var sessionId = created.Value;
        try
        {
            return await RunTurnAsync(request, sessionId, ct).ConfigureAwait(false);
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

    private static long TotalTokens(TurnUsage usage) => usage.InputTokens + usage.OutputTokens;

    [LoggerMessage(EventId = 800, Level = LogLevel.Warning,
        Message = "Closing detached session {SessionId} failed with {ErrorCode}; the run's own result is unaffected")]
    private static partial void LogCloseFailed(ILogger logger, string sessionId, AgentErrorCode errorCode);
}
