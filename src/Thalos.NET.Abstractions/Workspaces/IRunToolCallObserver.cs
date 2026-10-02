using ZeroAlloc.Authorization;

namespace Thalos.Workspaces;

/// <summary>One completed call of a run's tool on the run's remote tool endpoint, such as its sandbox.</summary>
/// <param name="RunId">The run the call was made for.</param>
/// <param name="Source">The tool source's name, e.g. <c>sandbox</c>.</param>
/// <param name="Tool">The tool's own name within <paramref name="Source"/>, e.g. <c>test</c>.</param>
/// <param name="Caller">The turn's caller, whose run claim routed the call.</param>
/// <param name="ResultText">The text the agent received, including an <c>error:</c> result.</param>
/// <param name="Elapsed">How long the call took, from resolving the run's endpoint to its result.</param>
public sealed record RunToolCall(Guid RunId, string Source, string Tool, ISecurityContext Caller, string ResultText, TimeSpan Elapsed);

/// <summary>
/// Told after every call a run made on its remote tool endpoint completes, with an answer or an <c>error:</c> result.
/// It is not told of calls refused before they were sent: a caller with no run, an invalid run claim, or a run with no
/// endpoint. A host uses it to record what a run's tools reported, such as a test run's outcome.
/// </summary>
/// <remarks>
/// An exception from an observer, including an <see cref="OperationCanceledException"/> of its own, is logged and
/// changes neither the call's result nor whether later observers are told; only the cancellation of the call's own
/// token stops it, as for <see cref="IRunWorkspaceObserver"/>.
/// <para>
/// The call waits for each observer, so an observer must finish quickly. The caller bounds the wait (Thalos.NET.Mcp's
/// <c>RemoteRunToolOptions.ObserverTimeout</c>); past it, the observer's token is cancelled, the delay is logged, and the
/// call goes on while the observer is left to finish on its own.
/// </para>
/// </remarks>
public interface IRunToolCallObserver
{
    /// <summary>Called once for each completed call, after its result is known and before the agent receives it.</summary>
    /// <param name="completed">The call.</param>
    /// <param name="ct">The call's cancellation token.</param>
    ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct);
}
