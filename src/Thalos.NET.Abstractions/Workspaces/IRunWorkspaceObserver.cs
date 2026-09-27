namespace Thalos.Workspaces;

/// <summary>Notified around a run workspace's lifecycle, so host code can react without polling <see cref="IRunWorkspaceProvider"/>.</summary>
/// <remarks>
/// An exception from an observer, including an <see cref="OperationCanceledException"/> of its own, is logged and does
/// not stop later observers being told; only the cancellation of the provider call's own token does.
/// </remarks>
public interface IRunWorkspaceObserver
{
    /// <summary>
    /// Called after <see cref="IRunWorkspaceProvider.CreateAsync"/> has made a workspace ready, and at no other time.
    /// </summary>
    /// <remarks>
    /// A host restart does not call it again for workspaces that already exist: nothing enumerates them at startup.
    /// An observer that keeps per-run state must rebuild it lazily, the first time the run needs it, through
    /// <see cref="IRunWorkspaceProvider.FindAsync"/>. <c>Thalos.NET.Mcp</c>'s <c>RunMcpServerRegistry</c> works this
    /// way: after a restart, its <c>WaitAllReadyAsync</c> looks the run's workspace up and starts the run's servers
    /// then, rather than waiting for a notification that never comes.
    /// </remarks>
    /// <param name="workspace">The workspace that is ready.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct);

    /// <summary>
    /// Called before the workspace's directory is removed, at least once: a removal that is retried, after a git
    /// failure or a cancellation, announces the workspace again. An implementation must treat a repeat as a no-op.
    /// </summary>
    /// <param name="workspace">The workspace about to be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct);
}
