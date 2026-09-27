using System.Text.Json.Serialization;
using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Source-generated serialization for the <see cref="WorkspaceSidecar"/> record <see cref="GitWorktreeWorkspaceProvider"/>
/// writes to <c>&lt;DataRoot&gt;/runs/&lt;run-id&gt;.workspace.json</c> and reads back in
/// <see cref="IRunWorkspaceProvider.FindAsync"/>, <see cref="IRunWorkspaceProvider.ListAsync"/> and
/// <see cref="IRunWorkspaceProvider.RemoveAsync"/> — the same type both ways, so the two stay in sync by construction.
/// The state is written as its name, so a record stays readable if the enum is ever reordered.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkspaceSidecar))]
internal sealed partial class GitWorkspaceJsonContext : JsonSerializerContext;
