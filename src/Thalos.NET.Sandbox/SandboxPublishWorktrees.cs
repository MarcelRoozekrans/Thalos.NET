using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// Wraps a <see cref="GitWorktreeWorkspaceProvider"/> rooted at &lt;DataRoot&gt;/publish. Not registered as
/// <see cref="IRunWorkspaceProvider"/>: its worktrees are where a sandboxed run's patch is applied for publishing, on the
/// trusted side, and no tool ever reaches them.
/// </summary>
/// <param name="inner">The provider, sharing its <see cref="GitWorkspaceOptions"/> with the sandbox provider's mirror store.</param>
public sealed class SandboxPublishWorktrees(GitWorktreeWorkspaceProvider inner)
{
    /// <inheritdoc cref="GitWorktreeWorkspaceProvider.CreateAsync"/>
    public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) => inner.CreateAsync(request, ct);

    /// <inheritdoc cref="GitWorktreeWorkspaceProvider.FindAsync"/>
    public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => inner.FindAsync(runId, ct);

    /// <inheritdoc cref="GitWorktreeWorkspaceProvider.RemoveAsync"/>
    public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => inner.RemoveAsync(runId, ct);
}
