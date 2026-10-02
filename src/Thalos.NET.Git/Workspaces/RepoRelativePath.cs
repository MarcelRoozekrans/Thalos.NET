using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// The lexical check for a repository-relative path that names an object in a git tree, not a file on disk. The same
/// rules as <see cref="WorkspacePath"/>'s lexical stage: nothing rooted, no <c>:</c>, no NUL, and no <c>.</c>, <c>..</c>
/// or <c>.git</c> segment. There is no filesystem to resolve against, so nothing more is checked.
/// </summary>
public static class RepoRelativePath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The failure for <paramref name="relativePath"/>, or <see langword="null"/> when it is acceptable.</summary>
    /// <param name="relativePath">The path to check.</param>
    public static AgentError? Validate(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Refuse(relativePath, "is blank");
        }

        if (relativePath.Contains('\0'))
        {
            return Refuse(relativePath, "contains a NUL character");
        }

        if (relativePath.Contains(':'))
        {
            return Refuse(relativePath, "contains a drive or alternate-data-stream qualifier");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            return Refuse(relativePath, "is rooted");
        }

        foreach (var segment in relativePath.Split(Separators))
        {
            if (segment is "." or "..")
            {
                return Refuse(relativePath, $"has a '{segment}' segment");
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(relativePath, "reaches into the git directory");
            }
        }

        return null;
    }

    private static AgentError Refuse(string? path, string reason) =>
        AgentError.Validation($"Path '{path}' {reason}.");
}
