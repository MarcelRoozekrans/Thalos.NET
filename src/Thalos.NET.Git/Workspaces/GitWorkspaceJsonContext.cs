using System.Text.Json.Serialization;
using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Source-generated serialization for the sidecar record <see cref="GitWorktreeWorkspaceProvider"/> writes to
/// <c>&lt;DataRoot&gt;/runs/&lt;run-id&gt;.workspace.json</c> and reads back in <see cref="IRunWorkspaceProvider.FindAsync"/>
/// and <see cref="IRunWorkspaceProvider.ListAsync"/> — the same type both ways, so the two stay in sync by construction.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RunWorkspace))]
internal sealed partial class GitWorkspaceJsonContext : JsonSerializerContext;
