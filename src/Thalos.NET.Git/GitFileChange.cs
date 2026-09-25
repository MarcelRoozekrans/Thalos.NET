namespace Thalos.Git;

/// <summary>One file's change between two commits, from <see cref="Workspaces.IRunWorkspaceGit.DiffStatAsync"/>.</summary>
/// <param name="Path">The file's repository-relative path.</param>
/// <param name="LinesAdded">Lines added. <c>0</c> for a binary file, which <c>git diff --numstat</c> reports as <c>-</c>.</param>
/// <param name="LinesDeleted">Lines deleted. <c>0</c> for a binary file, which <c>git diff --numstat</c> reports as <c>-</c>.</param>
public sealed record GitFileChange(string Path, int LinesAdded, int LinesDeleted);
