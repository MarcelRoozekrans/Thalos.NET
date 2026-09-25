using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>
/// Creates, finds, lists, and removes the git worktree backing each run. Implemented by <c>Thalos.NET.Git</c>;
/// consumed by <c>Thalos.NET.Mcp</c>'s <c>workspace__*</c> tools and by the host that dispatches run nodes.
/// </summary>
public interface IRunWorkspaceProvider
{
    /// <summary>Creates a new worktree for <paramref name="request"/>'s run and records it in the sidecar store.</summary>
    /// <param name="request">What to create the workspace from.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct);

    /// <summary>
    /// Looks up the workspace for <paramref name="runId"/> from the sidecar store, which survives host restarts.
    /// <see langword="null"/> when no workspace is recorded for the run.
    /// </summary>
    /// <param name="runId">The run to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct);

    /// <summary>Lists every workspace the sidecar store currently knows about, in no particular order.</summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct);

    /// <summary>Removes the run's worktree from disk and its sidecar record.</summary>
    /// <param name="runId">The run whose workspace is removed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct);
}
