using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>
/// Confines a path an agent supplies to a run's workspace. <see cref="Resolve"/> is the sole gate a
/// <c>workspace__*</c> tool puts a model-supplied path through before it touches disk — see
/// <see cref="RunWorkspace.Root"/>. The returned path carries the file system's own case (which may differ from
/// the input path's case on a case-insensitive volume). <see cref="Resolve"/> does not close the
/// check-to-use window between this call and the caller actually opening the file — a symlink can be swapped in
/// after this call returns but before the open — so a caller with a stronger requirement must additionally compare
/// the opened handle's real path against the workspace root.
/// </summary>
public static class WorkspacePath
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Resolves <paramref name="relativePath"/> inside <paramref name="workspaceRoot"/>, or fails. Refuses: blank;
    /// any path containing <c>':'</c> (a Windows drive qualifier or NTFS alternate data stream, and on every OS so a
    /// workspace path means the same thing everywhere); a <c>"."</c> or <c>".."</c> segment; a <c>".git"</c>
    /// segment case-insensitively, or a segment matching git's own NTFS short-name alias pattern
    /// <c>^git~\d+$</c> case-insensitively, checked against both the raw input and the fully resolved path; on
    /// Windows, a reserved device name, or a segment ending in a trailing dot or space; and any path whose existing
    /// ancestor, or itself, is a symlink or junction whose final target — itself re-canonicalised the same way, so a
    /// link that lands inside another link's target cannot escape — lies outside the root's own final target. A
    /// rooted or absolute <paramref name="relativePath"/> is refused by the containment check below, not by a
    /// separate early check: <see cref="Path.Combine(string, string)"/> discards <paramref name="workspaceRoot"/>
    /// entirely when the second argument is rooted, so the combined path can never land inside the root.
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

        var resolved = FollowLinks(root, full, relativePath);
        if (resolved.IsFailure)
            return resolved;

        return GitSegmentFailure(relativePath, root, resolved.Value) ?? resolved;
    }

    private static Result<string, AgentError>? ValidateSegments(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Failure(relativePath, "is blank");

        if (relativePath.Contains(':'))
            return Failure(relativePath, "contains a drive or alternate-data-stream qualifier");

        foreach (var segment in relativePath.Split(Separators))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
                return Failure(relativePath, "escapes the workspace via a '..' segment");

            if (string.Equals(segment, ".", StringComparison.Ordinal))
                return Failure(relativePath, $"has a '.' segment: '{segment}'");

            if (IsGitSegment(segment))
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

    /// <summary>
    /// Matches <c>".git"</c> case-insensitively, and git's own NTFS short-name (8.3) alias pattern
    /// <c>^git~\d+$</c> case-insensitively — the same heuristic git's <c>is_ntfs_dotgit</c> uses, refused outright
    /// regardless of what it currently resolves to.
    /// </summary>
    private static bool IsGitSegment(string segment) =>
        segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || IsGitNtfsAlias(segment);

    private static bool IsGitNtfsAlias(string segment)
    {
        if (segment.Length < 5 || !segment.StartsWith("git~", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 4; i < segment.Length; i++)
        {
            if (!char.IsAsciiDigit(segment[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Re-checks the fully resolved path — after link-following and after <see cref="Path.GetFullPath(string)"/>'s
    /// own 8.3 short-name expansion — for a <see cref="IsGitSegment"/> match. The raw-input check in
    /// <see cref="ValidateSegments"/> only sees what the caller typed; a junction or symlink to <c>.git</c>, or a
    /// short name that <see cref="Path.GetFullPath(string)"/> silently expands to <c>.git</c>, only shows up here.
    /// </summary>
    private static Result<string, AgentError>? GitSegmentFailure(string relativePath, string root, string resolvedPath)
    {
        var relative = Path.GetRelativePath(root, resolvedPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
            return null;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (IsGitSegment(segment))
                return Failure(relativePath, "reaches into the git directory");
        }

        return null;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var name = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Length != 4)
            return false;

        var prefix = name[..3];
        if (!prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) && !prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase))
            return false;

        // ASCII 1-9, or the superscript digits ¹ (U+00B9), ² (U+00B2), ³ (U+00B3) that some Windows versions also
        // treat as reserved, to block the old superscript-digit bypass of the ASCII-only check.
        return name[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }

    /// <summary>
    /// Walks from <paramref name="root"/> down to <paramref name="full"/>, following any symlink or junction it
    /// meets to its final target — itself re-canonicalised through <see cref="FinalPath"/> so a link that lands
    /// inside another link's target is resolved too, since <see cref="FileSystemInfo.ResolveLinkTarget"/> only
    /// follows the chain at the link's own final component and does not canonicalise the rest of that target's path
    /// on Unix — and requires every resolved target to stay inside <paramref name="root"/>.
    /// </summary>
    private static Result<string, AgentError> FollowLinks(string root, string full, string relativePath)
    {
        var relative = Path.GetRelativePath(root, full);
        var segments = string.Equals(relative, ".", StringComparison.Ordinal) ? [] : relative.Split(Path.DirectorySeparatorChar);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            var target = LinkTargetOf(current);
            if (target is null)
                continue;

            target = FinalPath(target);

            if (!IsContained(target, root))
                return Failure(relativePath, "passes through a symlink or junction whose target lies outside the workspace");

            current = target;
        }

        return Result<string, AgentError>.Success(current);
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
