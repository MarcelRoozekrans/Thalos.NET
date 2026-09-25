using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
public static partial class WorkspacePath
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Resolves <paramref name="relativePath"/> inside <paramref name="workspaceRoot"/>, or fails. Refuses: blank;
    /// any path containing <c>':'</c> (a Windows drive qualifier or NTFS alternate data stream, refused on every OS
    /// so a workspace path means the same thing everywhere); a <c>"."</c> or <c>".."</c> segment; a <c>".git"</c>
    /// segment case-insensitively, or a segment matching git's own NTFS short-name alias pattern <c>^git~\d+$</c>
    /// case-insensitively, checked against both the raw input and the fully resolved path; on Windows, a reserved
    /// device name, or a segment ending in a trailing dot or space. A rooted or absolute <paramref name="relativePath"/>
    /// is refused by the containment check below, not by a separate early check: <see cref="Path.Combine(string, string)"/>
    /// discards <paramref name="workspaceRoot"/> entirely when the second argument is rooted, so the combined path
    /// can never land inside the root.
    /// </summary>
    /// <remarks>
    /// Link resolution asks the operating system's own path canonicalisation for the deepest existing ancestor of
    /// the combined path — <c>realpath</c> on Linux and macOS, the handle's final path on Windows — rather than
    /// hand-walking symlinks and junctions segment by segment. A hand-written walk drifted from kernel semantics
    /// twice: it failed to re-canonicalise a link discovered while resolving another link, and it normalised a
    /// <c>".."</c> inside a link's recorded target as text, before the target itself had been followed, where the
    /// kernel applies <c>".."</c> only after following the link. Letting the kernel canonicalise avoids both classes
    /// of bug by construction. A dangling link, a link loop, or any other failure to resolve returns a failure
    /// <see cref="Result{T, E}"/> — never an exception — and is refused even where it would, if it resolved, land
    /// inside the workspace; that over-refusal is accepted. On a platform with no reachable kernel canonicalisation,
    /// every path that needs one is refused.
    /// </remarks>
    /// <param name="workspaceRoot">The workspace's root directory.</param>
    /// <param name="relativePath">The path an agent supplied, taken as relative to <paramref name="workspaceRoot"/>.</param>
    public static Result<string, AgentError> Resolve(string workspaceRoot, string relativePath)
    {
        var segmentFailure = ValidateSegments(relativePath);
        if (segmentFailure is { } failure)
            return failure;

        var rootCanonical = Canonicalize(Path.GetFullPath(workspaceRoot));
        if (rootCanonical.IsFailure)
            return Failure(relativePath, $"the workspace root could not be resolved: {rootCanonical.Error.Message}");
        var root = rootCanonical.Value;

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var ancestor = DeepestExistingAncestor(root, full);

        var ancestorCanonical = Canonicalize(ancestor);
        if (ancestorCanonical.IsFailure)
            return Failure(relativePath, $"could not be resolved: {ancestorCanonical.Error.Message}");

        var tail = Path.GetRelativePath(ancestor, full);
        var resolved = string.Equals(tail, ".", StringComparison.Ordinal)
            ? ancestorCanonical.Value
            : Path.Combine(ancestorCanonical.Value, tail);

        if (!IsContained(resolved, root))
            return Failure(relativePath, "escapes the workspace root");

        return GitSegmentFailure(relativePath, root, resolved) ?? Result<string, AgentError>.Success(resolved);
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
    /// Re-checks the fully resolved path for a <see cref="IsGitSegment"/> match. The raw-input check in
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
    /// Walks the segments between <paramref name="root"/> and <paramref name="full"/>, returning the deepest prefix
    /// that exists — using an <c>lstat</c>-style check, so a symlink or junction counts as existing even when its
    /// own target does not. Everything at or beyond the returned prefix does not exist yet, so it cannot contain a
    /// link: <see cref="Resolve"/> appends it, unresolved, onto the kernel-canonicalised ancestor.
    /// </summary>
    private static string DeepestExistingAncestor(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        var segments = string.Equals(relative, ".", StringComparison.Ordinal) ? [] : relative.Split(Path.DirectorySeparatorChar);

        var current = root;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            if (!LExists(candidate))
                break;

            current = candidate;
        }

        return current;
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> has a filesystem entry, without following a final symlink or
    /// junction — a dangling link still "exists" for this check, the same as POSIX <c>lstat</c>.
    /// <see cref="File.GetAttributes(string)"/> queries the entry itself on both Windows (<c>GetFileAttributesW</c>
    /// never follows reparse points) and Unix (an initial <c>lstat</c>), so this is already lstat-style on every
    /// supported platform.
    /// </summary>
    private static bool LExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // On Unix, GetAttributes still has to resolve every *intermediate* path component — lstat's
            // no-follow rule applies only to the final component — so a symlink loop earlier in the path (not the
            // segment being checked itself) surfaces here as ELOOP, not as a not-found error. Real lstat() would
            // fail the same way for a path whose intermediate component loops, so this is treated the same as
            // "does not exist yet": the walk stops before this segment, and Canonicalize's realpath call reports
            // the loop as a proper failure Result once it is asked to resolve the ancestor that stopped there.
            return false;
        }
    }

    /// <summary>
    /// Canonicalises <paramref name="path"/> using the operating system's own path resolution, so every symlink and
    /// junction along it — including one nested inside another's target, and a <c>".."</c> inside a link's target,
    /// applied only after the link is followed — is resolved exactly as the kernel would resolve it for a real
    /// open. Fails, rather than throwing, for a dangling link, a link loop, or any other resolution failure.
    /// </summary>
    private static Result<string, AgentError> Canonicalize(string path)
    {
        if (OperatingSystem.IsWindows())
            return CanonicalizeOnWindows(path);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return CanonicalizeOnUnix(path);

        return Result<string, AgentError>.Failure(
            AgentError.Validation($"path '{path}' cannot be resolved: no kernel path canonicalisation is reachable on this platform."));
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static Result<string, AgentError> CanonicalizeOnUnix(string path)
    {
        var resolved = Unix.RealPath(path);
        return resolved is not null
            ? Result<string, AgentError>.Success(resolved)
            : Result<string, AgentError>.Failure(AgentError.Validation($"path '{path}' could not be resolved (errno {Marshal.GetLastPInvokeError()})."));
    }

    [SupportedOSPlatform("windows")]
    private static Result<string, AgentError> CanonicalizeOnWindows(string path)
    {
        var resolved = Windows.GetFinalPath(path);
        return resolved is not null
            ? Result<string, AgentError>.Success(resolved)
            : Result<string, AgentError>.Failure(AgentError.Validation($"path '{path}' could not be resolved."));
    }

    private static bool IsContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static Result<string, AgentError> Failure(string relativePath, string reason) =>
        Result<string, AgentError>.Failure(AgentError.Validation($"path '{relativePath}' {reason}."));
}
