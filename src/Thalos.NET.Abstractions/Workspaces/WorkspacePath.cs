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
    /// a NUL character; any path containing <c>':'</c> (a Windows drive qualifier or NTFS alternate data stream,
    /// refused on every OS); a <c>"."</c> or <c>".."</c> segment; a <c>".git"</c> segment case-insensitively, or a
    /// segment matching git's own NTFS short-name alias pattern <c>^git~\d+$</c> case-insensitively, checked
    /// against both the raw input and the fully resolved path; on Windows, a reserved device name, or a segment
    /// ending in a trailing dot or space; a rooted or absolute <paramref name="relativePath"/>, refused outright as
    /// a lexical step before any filesystem access — including one that happens to resolve inside the workspace,
    /// which is refused too, since a caller using this contract correctly never has a reason to supply one; a
    /// workspace root that does not resolve to a directory; and any path whose resolution fails or lands outside
    /// the workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Link resolution asks the operating system's own path canonicalisation for the deepest existing ancestor of
    /// the combined path — <c>realpath</c> on Linux and macOS, the handle's final path on Windows — rather than
    /// hand-walking symlinks and junctions segment by segment. A hand-written walk drifted from kernel semantics
    /// repeatedly: it failed to re-canonicalise a link discovered while resolving another link; it normalised a
    /// <c>".."</c> inside a link's recorded target as text, before the target itself had been followed, where the
    /// kernel applies <c>".."</c> only after following the link; and its existence probe treated every filesystem
    /// error alike, so a path segment beyond the platform's length limit was silently treated as "does not exist",
    /// which stopped the probe early and left an unresolved, unverified tail that could itself contain a real
    /// symlink. Letting the kernel canonicalise removes the first two classes of bug by construction. The third is
    /// closed by a narrower rule for the probe itself: only <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> mean "does not exist" and stop the probe early; every other
    /// failure — a path too long, a permission error, a symlink loop, or anything else — refuses the whole
    /// resolution instead of guessing. Only given that rule does "the tail after the deepest existing ancestor
    /// cannot contain a link" actually hold: nothing that exists yet, and nothing the probe could not conclusively
    /// rule out, is ever appended unresolved.
    /// </para>
    /// <para>
    /// A dangling link, a link loop, a filesystem error during the existence probe, or any other failure to
    /// resolve returns a failure <see cref="Result{T, E}"/> — never an exception — and is refused even where it
    /// would, if it resolved, land inside the workspace; that over-refusal is accepted. On a platform with no
    /// reachable kernel canonicalisation, every path that needs one is refused.
    /// </para>
    /// <para>
    /// The segment checks name their reason, and echo only <paramref name="relativePath"/>: they look at nothing
    /// but the caller's own input. A rooted input, and every failure after the lexical checks, fail with one fixed
    /// message (see <see cref="GenericRefusal"/>) that carries no path, <c>errno</c> or Win32 error code. That
    /// includes an ancestor the kernel cannot canonicalise, which is where a link inside the workspace to a
    /// missing or unreadable host path fails, so the message cannot tell a missing host path from an unreadable
    /// one or from one that exists outside. <see cref="WorkspacePath"/> is static and has no logger, so the
    /// kernel's reason is dropped rather than logged. The message is not the only channel: a link whose target
    /// passes through a host directory on its way back into the workspace, such as
    /// <c>/host/dir/../../workspace/src</c>, resolves only if <c>/host/dir</c> exists, so success against refusal
    /// still reveals that much — not to whoever placed the link, but to whoever calls <see cref="Resolve"/>, which
    /// is the model working inside the workspace. A worktree created by <c>Thalos.NET.Git</c>'s
    /// <c>GitWorktreeWorkspaceProvider</c> closes this off at the source: it checks out every worktree with
    /// <c>core.symlinks=false</c>, so a symlink committed to the repository arrives as a plain text file, never a
    /// real link, and no path this method resolves inside such a workspace can carry that channel. The risk
    /// remains for a workspace populated some other way — by a tool that writes files into
    /// <paramref name="workspaceRoot"/> directly, or a provider that does not disable symlinks on checkout — where
    /// a real link can still exist, and this method's over-refusal on a link it cannot rule out is what limits the
    /// exposure for that case.
    /// </para>
    /// </remarks>
    /// <param name="workspaceRoot">The workspace's root directory.</param>
    /// <param name="relativePath">The path an agent supplied, taken as relative to <paramref name="workspaceRoot"/>.</param>
    public static Result<string, AgentError> Resolve(string workspaceRoot, string relativePath)
    {
        var segmentFailure = ValidateSegments(relativePath);
        if (segmentFailure is { } failure)
            return failure;

        // Lexical step, before any filesystem access: a rooted or absolute relativePath is refused outright,
        // regardless of where it would resolve — including one that happens to land inside the workspace, which a
        // caller using this contract correctly never has a reason to supply.
        if (Path.IsPathRooted(relativePath))
            return GenericRefusal();

        // Every failure from here on returns GenericRefusal, whatever its cause: the canonicalisation of a link's
        // outside target reports whether that host path exists or is readable, so no detail of it may reach the
        // message. WorkspacePath is static and has no logger, so that detail is dropped, not logged.
        var root = Canonicalize(Path.GetFullPath(workspaceRoot));
        if (root is null || !Directory.Exists(root))
            return GenericRefusal();

        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        var ancestor = DeepestExistingAncestor(root, full);
        if (ancestor is null)
            return GenericRefusal();

        var ancestorCanonical = Canonicalize(ancestor);
        if (ancestorCanonical is null)
            return GenericRefusal();

        var tail = Path.GetRelativePath(ancestor, full);
        var resolved = string.Equals(tail, ".", StringComparison.Ordinal)
            ? ancestorCanonical
            : Path.Combine(ancestorCanonical, tail);

        if (!IsContained(resolved, root) || ReachesGitDirectory(root, resolved))
            return GenericRefusal();

        return Result<string, AgentError>.Success(resolved);
    }

    private static Result<string, AgentError>? ValidateSegments(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Failure(relativePath, "is blank");

        if (relativePath.Contains('\0'))
            return Failure(relativePath, "contains a NUL character");

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
    private static bool ReachesGitDirectory(string root, string resolvedPath)
    {
        var relative = Path.GetRelativePath(root, resolvedPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
            return false;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (IsGitSegment(segment))
                return true;
        }

        return false;
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
    /// that exists — using an <c>lstat</c>-style existence probe (<see cref="LExists"/>), so a symlink or junction
    /// counts as existing even when its own target does not. Everything at or beyond the returned prefix does not
    /// exist yet, so it cannot contain a link and is appended, unresolved, onto the kernel-canonicalised ancestor —
    /// but only because <see cref="LExists"/> fails the whole walk, rather than guessing "does not exist", for any
    /// probe outcome other than a conclusive not-found.
    /// </summary>
    private static string? DeepestExistingAncestor(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        var segments = string.Equals(relative, ".", StringComparison.Ordinal) ? [] : relative.Split(Path.DirectorySeparatorChar);

        var current = root;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            var exists = LExists(candidate);
            if (exists is null)
                return null;

            if (exists == false)
                break;

            current = candidate;
        }

        return current;
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> has a filesystem entry, without following a final symlink or
    /// junction — a dangling link still "exists" for this check, the same as POSIX <c>lstat</c> — or returns
    /// <see langword="null"/> when that cannot be determined.
    /// <see cref="File.GetAttributes(string)"/> queries the entry itself on both Windows (<c>GetFileAttributesW</c>
    /// never follows reparse points) and Unix (an initial <c>lstat</c>), so a plain not-found result is already
    /// lstat-style on every supported platform. Only <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> are treated as "does not exist". Every other outcome —
    /// <see cref="PathTooLongException"/> or an <c>ENAMETOOLONG</c>-flavoured <see cref="IOException"/> for a
    /// segment beyond the platform's length limit, <see cref="UnauthorizedAccessException"/> for a permission
    /// error, an intermediate symlink loop, or anything else — fails closed instead: this is what makes
    /// <see cref="DeepestExistingAncestor"/>'s "the tail cannot contain a link" invariant actually true, rather
    /// than an unverified assumption a filesystem error could quietly violate.
    /// </summary>
    private static bool? LExists(string path)
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
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Canonicalises <paramref name="path"/> using the operating system's own path resolution, so every symlink and
    /// junction along it — including one nested inside another's target, and a <c>".."</c> inside a link's target,
    /// applied only after the link is followed — is resolved exactly as the kernel would resolve it for a real
    /// open. Returns <see langword="null"/>, rather than throwing, for a dangling link, a link loop, or any other
    /// resolution failure, and on a platform with no reachable kernel canonicalisation. The kernel's reason is
    /// dropped: it tells missing from unreadable, and <see cref="Resolve"/> must not.
    /// </summary>
    private static string? Canonicalize(string path)
    {
        if (OperatingSystem.IsWindows())
            return Windows.GetFinalPath(path);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return Unix.RealPath(path);

        return null;
    }

    private static bool IsContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static Result<string, AgentError> Failure(string relativePath, string reason) =>
        Result<string, AgentError>.Failure(AgentError.Validation($"path '{relativePath}' {reason}."));

    /// <summary>
    /// The single, fixed failure returned for a rooted or absolute input and for every failure after the lexical
    /// checks: a workspace root that cannot be canonicalised or is not a directory, a filesystem error while
    /// probing for the deepest existing ancestor, an ancestor the kernel cannot canonicalise — a dangling link, a
    /// link loop, a link whose target is missing or unreadable — a resolved path outside the workspace, and a
    /// resolved path inside the git directory. The message carries nothing derived from the caller's input or the
    /// host filesystem: no path, no <c>errno</c>, no Win32 error code. Every case returns the exact same message, so
    /// the message cannot tell a missing host path from an unreadable one or from one that exists outside.
    /// </summary>
    private static Result<string, AgentError> GenericRefusal() =>
        Result<string, AgentError>.Failure(AgentError.Validation("the path is not permitted."));
}
