using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>
/// Confines a path an agent supplies to a run's workspace. <see cref="Resolve"/> is the sole gate a
/// <c>workspace__*</c> tool puts a model-supplied path through before it touches disk — see
/// <see cref="RunWorkspace.Root"/>.
/// </summary>
public static class WorkspacePath
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Resolves <paramref name="relativePath"/> inside <paramref name="workspaceRoot"/>, or fails. Refuses: blank;
    /// rooted or drive-qualified input; any ".." segment; any ".git" segment, case-insensitive; and any path whose
    /// existing ancestor, or itself, is a symlink or junction whose final target lies outside the root's own final
    /// target.
    /// </summary>
    /// <param name="workspaceRoot">The workspace's root directory.</param>
    /// <param name="relativePath">The path an agent supplied, taken as relative to <paramref name="workspaceRoot"/>.</param>
    public static Result<string, AgentError> Resolve(string workspaceRoot, string relativePath)
    {
        var segmentFailure = ValidateSegments(relativePath);
        if (segmentFailure is { } failure)
            return failure;

        var root = FinalPath(Path.GetFullPath(workspaceRoot));
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsContained(full, root))
            return Failure(relativePath, "escapes the workspace root");

        return FollowLinks(root, full, relativePath);
    }

    private static Result<string, AgentError>? ValidateSegments(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Failure(relativePath, "is blank");

        if (Path.IsPathRooted(relativePath))
            return Failure(relativePath, "is rooted or drive-qualified");

        if (OperatingSystem.IsWindows() && relativePath.Contains(':'))
            return Failure(relativePath, "contains a drive or alternate-data-stream qualifier");

        foreach (var segment in relativePath.Split(Separators))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
                return Failure(relativePath, "escapes the workspace via a '..' segment");

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return Failure(relativePath, "reaches into the git directory");

            if (!OperatingSystem.IsWindows())
                continue;

            if (segment.Length > 0 && segment[^1] is '.' or ' ')
                return Failure(relativePath, $"has a segment ending in a trailing dot or space: '{segment}'");

            if (IsReservedDeviceName(segment))
                return Failure(relativePath, $"uses the reserved device name '{segment}'");
        }

        return null;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var name = dot < 0 ? segment : segment[..dot];

        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && name[3] is >= '1' and <= '9'
                && (name[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || name[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static Result<string, AgentError> FollowLinks(string root, string full, string relativePath)
    {
        var relative = Path.GetRelativePath(root, full);
        var segments = string.Equals(relative, ".", StringComparison.Ordinal) ? [] : relative.Split(Path.DirectorySeparatorChar);

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);

            var target = LinkTargetOf(current);
            if (target is null)
                continue;

            if (!IsContained(target, root))
                return Failure(relativePath, "passes through a symlink or junction whose target lies outside the workspace");

            var remaining = segments[(i + 1)..];
            current = remaining.Length == 0 ? target : Path.Combine([target, .. remaining]);
            full = current;
        }

        return Result<string, AgentError>.Success(full);
    }

    /// <summary>
    /// Walks <paramref name="path"/> from its volume root down, resolving every ancestor that is itself a symlink or
    /// junction to its final target, so a root reached through a link — a junction-backed workspace on Windows, or
    /// <c>/var</c> → <c>/private/var</c> on macOS — is canonicalised before the containment check.
    /// </summary>
    private static string FinalPath(string path)
    {
        var anchor = Path.GetPathRoot(path) ?? string.Empty;
        var relative = Path.GetRelativePath(anchor, path);
        var segments = string.Equals(relative, ".", StringComparison.Ordinal) ? [] : relative.Split(Path.DirectorySeparatorChar);

        var current = anchor;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            current = LinkTargetOf(current) ?? current;
        }

        return current;
    }

    private static string? LinkTargetOf(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }

        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }

        return null;
    }

    private static bool IsContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static Result<string, AgentError> Failure(string relativePath, string reason) =>
        Result<string, AgentError>.Failure(AgentError.Validation($"path '{relativePath}' {reason}."));
}
