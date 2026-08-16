using System.Runtime.CompilerServices;
using AI.Sentinel.Intervention;
using Microsoft.Extensions.AI;

namespace Thalos.Sentinel;

/// <summary>Turns <see cref="SentinelException"/> into <see cref="AgentTurnException"/> so the runtime returns <c>Quarantined</c>.</summary>
internal sealed class SentinelErrorMappingChatClient(IChatClient inner) : DelegatingChatClient(inner)
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
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Sentinel buffers the whole response before yielding, so mapping at the enumerator boundary is enough.
        IAsyncEnumerator<ChatResponseUpdate> e;
        try
        {
            e = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (SentinelException ex) { throw Map(ex); }

        await using (e.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try { moved = await e.MoveNextAsync().ConfigureAwait(false); }
                catch (SentinelException ex) { throw Map(ex); }
                if (!moved) yield break;
                yield return e.Current;
            }
        }
    }

    /// <summary>
    /// Detail is a single line built from the top detection, e.g. <c>"Critical: SEC-01 — Semantic match — high-severity threat pattern"</c>.
    /// AI.Sentinel 2.0.1 puts only the top detection in <see cref="SentinelException.PipelineResult"/>; rate-limit rejections carry no result at all,
    /// in which case the exception message is used.
    /// </summary>
    private static AgentTurnException Map(SentinelException ex)
    {
        var result = ex.PipelineResult;
        var top = result?.Detections.MaxBy(d => d.Severity);
        var detail = top is null
            ? (result is null ? ex.Message : result.MaxSeverity.ToString())
            : $"{top.Severity}: {top.DetectorId.Value} — {top.Reason}";
        return new AgentTurnException(AgentError.Quarantined("Blocked by AI.Sentinel.", detail), ex);
    }
}
