namespace Thalos.Workspaces;

/// <summary>How long a run's workspace or sandbox may exist without a record of its run before a sweep removes it.</summary>
public static class RunWorkspaceGrace
{
    /// <summary>
    /// A workspace exists before its run row does: the host creates it, then starts the run under its id. A sweep or a
    /// reconcile inside that window must not remove it (ruling R9). The one definition, shared by
    /// <c>RunWorkspaceSweeper</c> and the sandbox provider's reconcile.
    /// </summary>
    public static readonly TimeSpan Orphan = TimeSpan.FromMinutes(10);
}
