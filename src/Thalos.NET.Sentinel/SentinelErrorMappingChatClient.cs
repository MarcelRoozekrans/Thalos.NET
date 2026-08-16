using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AI.Sentinel.Detection;
using AI.Sentinel.Intervention;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Thalos.Sentinel;

/// <summary>
/// Turns <see cref="SentinelException"/> into <see cref="AgentTurnException"/> so the runtime returns <c>Quarantined</c>
/// (or <c>ProviderError</c> for Sentinel rate-limit rejections), and unwraps AI.Sentinel's inner-client wrapper so provider
/// exceptions reach the runtime's mapping unchanged.
/// </summary>
internal sealed partial class SentinelErrorMappingChatClient(IChatClient inner, ILogger? logger = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (SentinelException ex)
        {
            throw Map(ex);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is { } inner && IsInnerClientWrapper(ex))
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SentinelChatClient returns a lazy iterator: nothing runs until the first MoveNextAsync, and Sentinel buffers the whole
        // response before yielding anything, so mapping at MoveNextAsync is sufficient (no exceptions escape GetAsyncEnumerator).
        var e = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (e.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await e.MoveNextAsync().ConfigureAwait(false);
                }
                catch (SentinelException ex)
                {
                    throw Map(ex);
                }
                catch (InvalidOperationException ex) when (ex.InnerException is { } inner && IsInnerClientWrapper(ex))
                {
                    ExceptionDispatchInfo.Capture(inner).Throw();
                    throw;
                }

                if (!moved)
                {
                    yield break;
                }

                yield return e.Current;
            }
        }
    }

    /// <summary>
    /// AI.Sentinel 2.0.1 catches every exception from the inner client and rethrows it as
    /// <c>SentinelError.PipelineFailure.ToException()</c> = <see cref="InvalidOperationException"/> with message
    /// <c>"Inner client failed."</c> (buffered) or <c>"Inner client streaming failed."</c> (streaming) and the original as inner.
    /// </summary>
    private static bool IsInnerClientWrapper(InvalidOperationException ex) => ex.Message.StartsWith("Inner client", StringComparison.Ordinal);

    /// <summary>
    /// Quarantine: detail is <c>"{Severity}: {DetectorId}"</c> of the top detection — never the detector's reason text (SEC-02 echoes
    /// credential fragments, PlaceholderText echoes matches); the reason is logged instead. AI.Sentinel 2.0.1 puts only the top
    /// detection in <see cref="SentinelException.PipelineResult"/>. Rate-limit rejections carry no result and are not quarantines.
    /// </summary>
    private AgentTurnException Map(SentinelException ex)
    {
        var result = ex.PipelineResult;
        if (result is null)
        {
            return new AgentTurnException(AgentError.ProviderError("AI.Sentinel rate limit exceeded.", ex.Message), ex);
        }

        var top = result.Detections.MaxBy(d => d.Severity);
        var severity = top?.Severity ?? result.MaxSeverity;
        var detector = top?.DetectorId.Value ?? "unknown";
        if (logger is not null)
        {
            LogQuarantined(logger, severity, detector, top?.Reason ?? string.Empty);
        }

        return new AgentTurnException(AgentError.Quarantined("Blocked by AI.Sentinel.", $"{severity}: {detector}"), ex);
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "AI.Sentinel quarantined a turn: {Severity} {Detector}: {Reason}")]
    private static partial void LogQuarantined(ILogger logger, Severity severity, string detector, string reason);
}
