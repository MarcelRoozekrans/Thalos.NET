namespace Thalos.Git.Workspaces;

/// <summary>Where a run's sidecar record stands in its create.</summary>
internal enum WorkspaceSidecarState
{
    /// <summary>
    /// Claimed but not finished: the claimant owns the run and is preparing the mirror and worktree, possibly in
    /// another process. A remove refuses such a record until it is older than the grace period.
    /// </summary>
    Provisional,

    /// <summary>The create completed; the worktree exists and observers were told it is ready.</summary>
    Ready,
}
