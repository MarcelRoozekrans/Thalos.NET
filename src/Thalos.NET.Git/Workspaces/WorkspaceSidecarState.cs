namespace Thalos.Git.Workspaces;

/// <summary>Where a run's sidecar record stands in its create or its removal.</summary>
internal enum WorkspaceSidecarState
{
    /// <summary>
    /// Claimed but not finished: the claimant owns the run and is preparing the mirror and worktree, possibly in
    /// another process. A remove refuses such a record until it is older than the grace period.
    /// </summary>
    Provisional,

    /// <summary>The create completed; the worktree exists and observers were told it is ready.</summary>
    Ready,

    /// <summary>
    /// A removal started: the record was marked before observers were told the workspace is going, so
    /// <see cref="GitWorktreeWorkspaceProvider.FindAsync"/> no longer reports it. It stays in this state if the git
    /// side of the removal fails, until a later <see cref="GitWorktreeWorkspaceProvider.RemoveAsync"/> finishes it.
    /// </summary>
    Removing,
}
