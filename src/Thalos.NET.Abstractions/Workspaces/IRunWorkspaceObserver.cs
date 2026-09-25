namespace Thalos.Workspaces;

/// <summary>Notified around a run workspace's lifecycle, so host code can react without polling <see cref="IRunWorkspaceProvider"/>.</summary>
public interface IRunWorkspaceObserver
{
    /// <summary>Called after <see cref="IRunWorkspaceProvider.CreateAsync"/>, and after a restart finds an existing workspace.</summary>
    /// <param name="workspace">The workspace that is ready.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct);

    /// <summary>Called before the workspace's directory is removed.</summary>
    /// <param name="workspace">The workspace about to be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct);
}
