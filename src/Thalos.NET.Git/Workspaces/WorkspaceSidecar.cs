using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// What <see cref="GitWorktreeWorkspaceProvider"/> writes to <c>&lt;DataRoot&gt;/runs/&lt;run-id&gt;.workspace.json</c>:
/// the workspace plus the state of its create. The file is only ever published whole, by moving a complete temp
/// file into place, so a reader sees either no record or a complete one.
/// </summary>
/// <param name="State">Whether the create that claimed this run is still in flight or completed.</param>
/// <param name="Workspace">The workspace; its <see cref="RunWorkspace.SolutionPath"/> is unresolved while provisional.</param>
internal sealed record WorkspaceSidecar(WorkspaceSidecarState State, RunWorkspace Workspace);
