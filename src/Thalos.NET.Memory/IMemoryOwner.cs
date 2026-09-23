using ZeroAlloc.Authorization;

namespace Thalos.Memory;

/// <summary>
///     Implemented by an <see cref="ISecurityContext"/> whose memory should be stored under an id other than
///     <see cref="ISecurityContext.Id"/>. Exists for callers whose authorization identity is deliberately
///     short-lived: a workflow run identifies itself per run so it stays auditable, but its memory must
///     outlive the run or nothing it learns is ever readable again.
/// </summary>
public interface IMemoryOwner
{
    /// <summary>Stable owner id for this caller's memories. Blank or anonymous falls back to <see cref="ISecurityContext.Id"/>.</summary>
    string MemoryOwnerId { get; }

    /// <summary>
    ///     When true, every memory this caller writes is pinned to the turn's agent and the tool's
    ///     <c>shared</c> parameter is ignored.
    /// </summary>
    bool PinMemoriesToAgent { get; }
}
