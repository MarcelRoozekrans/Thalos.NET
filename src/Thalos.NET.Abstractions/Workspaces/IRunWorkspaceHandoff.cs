using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>Hands a run's changes to host code for publishing.</summary>
public interface IRunWorkspaceHandoff
{
    /// <summary>A workspace whose Root is a host directory holding the run's changes, ready for host code to commit and push. Idempotent.</summary>
    /// <remarks>
    /// <para>
    /// How the changes are held depends on the provider, and decides how host code must commit them. A local git
    /// worktree provider returns the run's own worktree, where the agent's files were written to disk and never staged:
    /// commit it the usual way, which stages from disk.
    /// </para>
    /// <para>
    /// A sandboxed provider returns a clean publish worktree where the run's patch has been applied and <b>staged in the
    /// index</b> by the publish-side protected-path check, and that index is what the check passed. Commit it with
    /// <c>GitCommitRequest.CommitStagedIndex</c> set (<c>Thalos.NET.Git</c>), which commits the index as it stands.
    /// Staging it again from disk would drop an added file the worktree's <c>.gitignore</c> matches and, under
    /// <c>core.fileMode=false</c>, a mode change, so the commit would differ from what was checked.
    /// </para>
    /// </remarks>
    ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct);
}
