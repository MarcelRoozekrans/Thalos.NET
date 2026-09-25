using System.Runtime.Versioning;
using AwesomeAssertions;
using Thalos.Workspaces;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class WorkspacePathTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _outside;

    public WorkspacePathTests()
    {
        _root = Directory.CreateTempSubdirectory("thalos-workspace-path-").FullName;
        _workspace = Path.Combine(_root, "workspace");
        _outside = Path.Combine(_root, "outside");

        Directory.CreateDirectory(Path.Combine(_workspace, "src", "Lib"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        try
        {
            DeleteTree(_root);
        }
        catch (IOException)
        {
            // best-effort cleanup; a lingering junction/symlink target must not fail the test run
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/>. Unlike <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c>,
    /// a directory that is itself a symlink or junction is unlinked without following it into its target — the
    /// target belongs to another test fixture's tree, or lives outside the temp root entirely, and must not be
    /// touched by this test's teardown.
    /// </summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;

        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var child in Directory.GetDirectories(path))
            DeleteTree(child);

        foreach (var file in Directory.GetFiles(path))
            File.Delete(file);

        Directory.Delete(path, recursive: false);
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("src/.GIT/HEAD")]
    [InlineData("")]
    public void Refuses_escapes_and_the_git_directory(string path) =>
        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);

    [Fact]
    public void Allows_a_normal_nested_path() =>
        WorkspacePath.Resolve(_workspace, "src/Lib/Class1.cs").Value.Should().Be(Path.Combine(_workspace, "src", "Lib", "Class1.cs"));

    [Fact]
    public void Refuses_a_dot_dot_segment_even_when_it_nets_out_inside_the_workspace() =>
        // "src/../src/Class1.cs" collapses to a path inside the workspace, so only the segment-level ".."
        // check — not the containment check on the collapsed path — can refuse it. On Windows this assertion is
        // NOT falsifiable by removing the ".." check alone: ".." also ends in '.', so the Windows-only trailing-dot
        // check independently catches it too. It is falsifiable only on the ubuntu leg, where the trailing-dot
        // check does not run at all.
        WorkspacePath.Resolve(_workspace, "src/../src/Class1.cs").IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(".")]
    [InlineData("src/./x.cs")]
    [InlineData("./x.cs")]
    public void Refuses_a_single_dot_segment(string path) =>
        // Refused on every OS, uniformly, with a message naming the dot segment — previously Windows refused this
        // with a misleading "trailing dot" message (an artefact of a different check) and Linux allowed it outright.
        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);

    [Theory]
    [InlineData("git~1/config")]
    [InlineData("GIT~1/config")]
    [InlineData("src/git~22/file")]
    public void Refuses_gits_ntfs_alias_pattern(string path) =>
        // Refused outright by the raw-input segment check, on every OS, regardless of whether anything on disk
        // actually resolves to it — the same conservative rule git's own is_ntfs_dotgit applies.
        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);

    [SkippableFact]
    public void Refuses_the_8_3_short_name_of_the_git_directory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "8.3 short names are an NTFS/Windows concept");

        var gitDir = Path.Combine(_workspace, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config"), "test");

        var shortNamePath = Path.Combine(_workspace, "GIT~1");
        if (!Directory.Exists(shortNamePath))
            FailOrSkip("resolve the .git directory's 8.3 short name GIT~1 — 8dot3name generation may be disabled on this volume", null);

        // "GIT~1" itself already matches the raw-input git~N alias check, so this is refused outright before any
        // resolution happens — this test's distinct value is confirming, against a real .git directory on a real
        // NTFS volume (via "dir /x"), that Windows really does generate exactly that alias, so the pattern this
        // code refuses is the pattern Windows actually produces, not a guess. Path.GetFullPath separately (and
        // silently) expands "GIT~1" to ".git" when the target exists, which is what the post-resolution
        // git-segment check exists to catch for a case the raw check cannot see — see
        // Refuses_a_junction_or_symlink_to_the_git_directory, below, for that isolated case.
        WorkspacePath.Resolve(_workspace, "GIT~1/config").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_junction_or_symlink_to_the_git_directory()
    {
        var gitDir = Path.Combine(_workspace, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config"), "test");

        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "togit"), gitDir);

        // A junction on Windows, a symlink on Linux — the raw input "togit" does not look like ".git"; only the
        // resolved target does, so this is refused by the post-resolution git-segment check.
        WorkspacePath.Resolve(_workspace, "togit/config").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_link_nested_inside_another_links_target()
    {
        // ws/inner -> _outside (escapes the workspace)
        // ws/viainner -> ws/inner/sub (records the literal string "ws/inner/sub" as its target — a path that
        // itself passes through "inner", which is a second link)
        //
        // FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) only follows the chain at viainner's own last
        // component; on Unix it does not canonicalise "inner" inside that recorded target string. Unfixed, Resolve
        // would report success and a write would land in outside/sub, physically outside the workspace, because
        // the OS resolves "inner" transparently when the path is actually used.
        Directory.CreateDirectory(Path.Combine(_outside, "sub"));

        var inner = Path.Combine(_workspace, "inner");
        CreateDirectoryLinkOrSkip(inner, _outside);

        var viaInner = Path.Combine(_workspace, "viainner");
        CreateDirectoryLinkOrSkip(viaInner, Path.Combine(_workspace, "inner", "sub"));

        WorkspacePath.Resolve(_workspace, "viainner/x.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_three_level_link_chain_that_escapes()
    {
        // ws/a -> ws/b/c, ws/b -> ws/d/e, ws/d -> _outside. Each link's target must exist for mklink /J to accept
        // it, so these are created in dependency order: _outside/e/c is real; d aliases _outside directly; b's
        // target "ws/d/e" is only reachable by first following d; a's target "ws/b/c" is only reachable by
        // following b then d. A two-level chain (Refuses_a_link_nested_inside_another_links_target, above) is not
        // enough to prove re-canonicalisation is itself recursive — this needs a third hop.
        Directory.CreateDirectory(Path.Combine(_outside, "e", "c"));

        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "d"), _outside);
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "b"), Path.Combine(_workspace, "d", "e"));
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "a"), Path.Combine(_workspace, "b", "c"));

        WorkspacePath.Resolve(_workspace, "a/n3.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_three_level_link_chain_that_reaches_the_git_directory()
    {
        // Same chain shape as above, landing on .git instead of outside the workspace, proving the post-resolution
        // git-segment check sees the fully re-canonicalised result of a multi-hop chain, not just a single hop.
        var gitDir = Path.Combine(_workspace, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "e", "c"));

        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "d"), gitDir);
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "b"), Path.Combine(_workspace, "d", "e"));
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "a"), Path.Combine(_workspace, "b", "c"));

        WorkspacePath.Resolve(_workspace, "a/n3.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_link_target_containing_dot_dot_in_relative_form()
    {
        // ws/sublink -> _outside/deep/leaf (absolute target); ws/reldd -> "sublink/.." (a *relative* target,
        // relative to reldd's own directory, i.e. ws/). A hand-rolled resolver that normalises ".." as text before
        // following "sublink" collapses this back to ws itself; the kernel applies ".." only after following
        // "sublink", landing at _outside/deep — outside the workspace. Needs a real symlink: a junction cannot
        // record a relative target.
        Directory.CreateDirectory(Path.Combine(_outside, "deep", "leaf"));
        CreateRealSymlinkOrSkip(Path.Combine(_workspace, "sublink"), Path.Combine(_outside, "deep", "leaf"));
        CreateRealSymlinkOrSkip(Path.Combine(_workspace, "reldd"), Path.Combine("sublink", ".."));

        WorkspacePath.Resolve(_workspace, "reldd/rd.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_link_target_containing_dot_dot_in_absolute_form()
    {
        // Same escape, but the link's recorded target is itself an absolute path ending in "sublink/..", not a
        // relative one — both forms must be resolved by the kernel, not normalised as text.
        Directory.CreateDirectory(Path.Combine(_outside, "deep", "leaf"));
        CreateRealSymlinkOrSkip(Path.Combine(_workspace, "sublink"), Path.Combine(_outside, "deep", "leaf"));
        CreateRealSymlinkOrSkip(Path.Combine(_workspace, "absdd"), Path.Combine(_workspace, "sublink", ".."));

        WorkspacePath.Resolve(_workspace, "absdd/rd.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_self_referencing_link_without_throwing()
    {
        var selfLink = Path.Combine(_workspace, "self-loop");
        CreateRealSymlinkOrSkip(selfLink, selfLink);

        var act = () => WorkspacePath.Resolve(_workspace, "self-loop/x.txt");

        act.Should().NotThrow();
        act().IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_two_link_loop_without_throwing()
    {
        var linkA = Path.Combine(_workspace, "loop-a");
        var linkB = Path.Combine(_workspace, "loop-b");
        CreateRealSymlinkOrSkip(linkA, linkB); // linkB does not exist yet; a symlink may dangle at creation
        CreateRealSymlinkOrSkip(linkB, linkA); // now linkA -> linkB -> linkA is a genuine loop

        var act = () => WorkspacePath.Resolve(_workspace, "loop-a/x.txt");

        act.Should().NotThrow();
        act().IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_dangling_link()
    {
        // A junction/symlink whose target has been removed. Built without needing symlink privilege on Windows: a
        // junction requires an existing target at creation time, so one is created, linked to, then deleted.
        var dangling = Path.Combine(_workspace, "dangling");
        CreateDanglingDirectoryLinkOrSkip(dangling);

        WorkspacePath.Resolve(_workspace, "dangling/x.txt").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_everything_under_a_drive_root_workspace()
    {
        // A drive-root (or filesystem-root, on Linux) workspace is not a supported configuration, but it must fail
        // safe: refuse every path, rather than silently allowing access to the whole volume.
        var driveRoot = Path.GetPathRoot(_workspace)!;

        WorkspacePath.Resolve(driveRoot, "foo.txt").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_workspace_root_that_is_a_file()
    {
        var rootFile = Path.Combine(_root, "root-is-a-file.txt");
        File.WriteAllText(rootFile, "not a directory");

        WorkspacePath.Resolve(rootFile, "foo.txt").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_nul_character_without_throwing()
    {
        var act = () => WorkspacePath.Resolve(_workspace, "foo\0bar.txt");

        act.Should().NotThrow();
        act().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_an_absolute_path_that_lies_inside_the_workspace()
    {
        // The rooted check's own red: remove it, and this succeeds wrongly, because Path.Combine discards the
        // workspace root when the second argument is rooted, and the absolute input happens to equal the combined
        // path anyway — so plain containment does not catch it. A caller using this contract correctly only ever
        // supplies a relative path, so an absolute one is refused regardless of where it points.
        var absoluteInsideWorkspace = Path.Combine(_workspace, "src", "Lib", "Class1.cs");

        WorkspacePath.Resolve(_workspace, absoluteInsideWorkspace).IsFailure.Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("/root/.bashrc")]
    [InlineData("/root/nope/x")]
    public void Refuses_absolute_host_paths_outside_the_workspace_without_throwing(string path)
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "these specific host paths are POSIX-only");

        var act = () => WorkspacePath.Resolve(_workspace, path);

        act.Should().NotThrow();
        act().IsFailure.Should().BeTrue(path);
    }

    [SkippableFact]
    public void Refuses_absolute_host_paths_with_the_same_message_whether_or_not_they_exist()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "these specific host paths are POSIX-only");

        // /root/.bashrc exists in the base image; /root/nope/x does not. Both are refused before either the
        // existence of /root/.bashrc or the non-existence of /root/nope is ever probed, and with the exact same
        // fixed message, so the caller cannot tell which host path was real.
        var existing = WorkspacePath.Resolve(_workspace, "/root/.bashrc");
        var missing = WorkspacePath.Resolve(_workspace, "/root/nope/x");

        existing.IsFailure.Should().BeTrue();
        missing.IsFailure.Should().BeTrue();
        existing.Error.Message.Should().Be(missing.Error.Message);
    }

    [SkippableFact]
    public void Refuses_an_unreadable_directory_inside_the_workspace_without_throwing()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "POSIX permission bits");
            return;
        }

        RefusesAnUnreadableDirectoryWithoutThrowing();
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private void RefusesAnUnreadableDirectoryWithoutThrowing()
    {
        var locked = Path.Combine(_workspace, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            // chmod 000 has no effect on root; skip rather than silently pass if this process can bypass it.
            Skip.If(CanListDespiteNoPermissions(locked), "running with a privilege that bypasses POSIX permission bits");

            var act = () => WorkspacePath.Resolve(_workspace, "locked/x.txt");

            act.Should().NotThrow();
            act().IsFailure.Should().BeTrue();
        }
        finally
        {
            // restore access so the fixture's own teardown can remove it
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public void Refuses_links_to_a_missing_an_unreadable_and_an_existing_outside_target_with_one_message()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "POSIX permission bits");
            return;
        }

        RefusesLinksToMissingUnreadableAndExistingTargetsWithOneMessage();
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private void RefusesLinksToMissingUnreadableAndExistingTargetsWithOneMessage()
    {
        // Links reach the workspace through repository content checked out with symlinks. Resolving the link
        // itself makes the kernel canonicalise its outside target: ENOENT for the missing one, EACCES for the one
        // under a mode-000 directory, success for the existing one. If any of those outcomes reached the message,
        // the model could probe whether a host path exists or is readable.
        var locked = Path.Combine(_outside, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "secret");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            Skip.If(CanListDespiteNoPermissions(locked), "running with a privilege that bypasses POSIX permission bits");

            CreateRealSymlinkOrSkip(Path.Combine(_workspace, "to-missing"), Path.Combine(_outside, "nope"));
            CreateRealSymlinkOrSkip(Path.Combine(_workspace, "to-unreadable"), Path.Combine(locked, "secret.txt"));
            CreateRealSymlinkOrSkip(Path.Combine(_workspace, "to-existing"), Path.Combine(_outside, "secret.txt"));

            var missing = WorkspacePath.Resolve(_workspace, "to-missing");
            var unreadable = WorkspacePath.Resolve(_workspace, "to-unreadable");
            var existing = WorkspacePath.Resolve(_workspace, "to-existing");

            new[] { missing.IsFailure, unreadable.IsFailure, existing.IsFailure }.Should().AllBeEquivalentTo(true);
            new[] { missing.Error.Message, unreadable.Error.Message, existing.Error.Message }
                .Distinct(StringComparer.Ordinal).Should().ContainSingle("a missing, an unreadable and an existing host target must be indistinguishable");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public void Refuses_directory_links_to_a_missing_and_an_existing_outside_target_with_one_message()
    {
        // The cross-platform half of the test above: a junction on Windows, a symlink elsewhere. On Windows the
        // kernel reports ERROR_FILE_NOT_FOUND for the dangling junction and succeeds for the existing one.
        var gone = Path.Combine(_outside, "gone");
        Directory.CreateDirectory(gone);
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "to-gone"), gone);
        Directory.Delete(gone);

        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "to-outside"), _outside);

        var missing = WorkspacePath.Resolve(_workspace, "to-gone");
        var existing = WorkspacePath.Resolve(_workspace, "to-outside");

        missing.IsFailure.Should().BeTrue();
        existing.IsFailure.Should().BeTrue();
        missing.Error.Message.Should().Be(existing.Error.Message);
    }

    [SkippableFact]
    public void Refuses_a_link_at_the_bottom_of_a_directory_chain_beyond_the_platform_length_limit()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "PATH_MAX is a POSIX concept");

        // A real chain of directories inside the workspace whose absolute path is longer than PATH_MAX (4096 on
        // Linux), with a symlink to _outside at its bottom. An absolute path this long cannot be created or
        // probed in one call, so the chain is built by a shell doing relative mkdir and cd -P, one level at a time;
        // -P keeps the shell from rebuilding the absolute path, which it could not chdir to.
        // Past PATH_MAX, the existence probe fails with ENAMETOOLONG before it reaches "escape". If that failure
        // were read as "does not exist", "…/escape/x.txt" would be appended unresolved onto a canonicalised
        // ancestor, pass the textual containment check, and be returned; a writer that walks down the chain
        // relatively, as the shell does here, would then follow "escape" and write into _outside.
        var segment = new string('d', 200);
        const int depth = 24; // 24 * 201 bytes, plus the temp root, is over 4096
        var chain = string.Join('/', Enumerable.Repeat(segment, depth));

        RunShell(
            _workspace,
            $"i=0; while [ $i -lt {depth} ]; do mkdir {segment} && cd -P {segment} || exit 1; i=$((i+1)); done; ln -s '{_outside}' escape");

        try
        {
            Path.Combine(_workspace, chain).Length.Should().BeGreaterThan(4096, "the chain must really exceed PATH_MAX");

            WorkspacePath.Resolve(_workspace, $"{chain}/escape/x.txt").IsFailure.Should().BeTrue();
        }
        finally
        {
            RunShell(
                _workspace,
                $"i=0; while [ $i -lt {depth} ]; do cd -P {segment} || exit 1; i=$((i+1)); done; rm escape; "
                + $"i=0; while [ $i -lt {depth} ]; do cd -P .. && rmdir {segment} || exit 1; i=$((i+1)); done");
        }
    }

    [Fact]
    public void Allows_a_path_inside_the_workspace_beyond_the_windows_max_path()
    {
        // MAX_PATH is 260 on Windows. Without the extended-length prefix, CreateFileW refuses a longer existing
        // ancestor with ERROR_PATH_NOT_FOUND and a legitimate write is refused. On Linux this path is well within
        // PATH_MAX and must resolve too.
        var deep = Path.Combine(Enumerable.Repeat(new string('d', 60), 6).Prepend(_workspace).ToArray());
        Directory.CreateDirectory(deep);
        var relative = Path.GetRelativePath(_workspace, Path.Combine(deep, "Class1.cs"));

        var result = WorkspacePath.Resolve(_workspace, relative);

        Path.Combine(deep, "Class1.cs").Length.Should().BeGreaterThan(260, "the path must really exceed MAX_PATH");
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(deep, "Class1.cs"));
    }

    [SkippableFact]
    public void Allows_a_path_inside_a_unc_workspace_beyond_the_windows_max_path()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "UNC paths are a Windows concept");

        // The workspace reached through the local administrative share, so its root is \\localhost\C$\...
        // An extended-length UNC path is \\?\UNC\server\share\..., not \\?\ followed by the UNC path.
        var drive = Path.GetPathRoot(_workspace)!;
        var share = $@"\\localhost\{drive[0]}$\";
        Skip.IfNot(Directory.Exists(share), $"the administrative share {share} is not reachable on this machine");

        var uncWorkspace = share + _workspace[drive.Length..];
        var deep = Path.Combine(Enumerable.Repeat(new string('d', 60), 6).Prepend(uncWorkspace).ToArray());
        Directory.CreateDirectory(deep);
        var relative = Path.GetRelativePath(uncWorkspace, Path.Combine(deep, "Class1.cs"));

        var result = WorkspacePath.Resolve(uncWorkspace, relative);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(uncWorkspace, relative));
    }

    private static void RunShell(string workingDirectory, string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"shell script failed with exit code {process.ExitCode}: {stderr}");
    }

    private static bool CanListDespiteNoPermissions(string path)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void Refuses_a_sibling_directory_that_merely_shares_the_workspace_name_as_a_prefix()
    {
        // Caught by the ".." segment check before containment is even considered — kept as a regression test for
        // that check specifically. Refuses_a_link_target_that_is_a_sibling_sharing_the_workspace_name_as_a_prefix,
        // below, isolates the containment separator check on its own, through a route the ".." check cannot reach.
        Directory.CreateDirectory(Path.Combine(_root, "workspace-evil"));

        WorkspacePath.Resolve(_workspace, "../workspace-evil/x").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_link_target_that_is_a_sibling_sharing_the_workspace_name_as_a_prefix()
    {
        // A link target reaches the containment check directly, with no relativePath ".." segment involved, so
        // this is the one case a StartsWith(root) without the trailing separator would wrongly accept: the target
        // "…\workspace-evil" textually starts with "…\workspace".
        var evilSibling = Path.Combine(_root, "workspace-evil");
        Directory.CreateDirectory(evilSibling);
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "escape-evil"), evilSibling);

        WorkspacePath.Resolve(_workspace, "escape-evil/x").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_directory_link_pointing_out()
    {
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "escape"), _outside);

        WorkspacePath.Resolve(_workspace, "escape/secret.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Refuses_a_file_symlink_pointing_out()
    {
        CreateFileSymlinkOrSkip(Path.Combine(_workspace, "link.txt"), Path.Combine(_outside, "secret.txt"));

        WorkspacePath.Resolve(_workspace, "link.txt").IsFailure.Should().BeTrue();
    }

    [SkippableFact]
    public void Allows_a_link_that_stays_inside()
    {
        CreateDirectoryLinkOrSkip(Path.Combine(_workspace, "alias"), Path.Combine(_workspace, "src"));

        WorkspacePath.Resolve(_workspace, "alias/x.cs").IsSuccess.Should().BeTrue();
    }

    [SkippableFact]
    public void Allows_a_normal_nested_path_when_the_workspace_root_is_itself_reached_through_a_junction()
    {
        var real = Path.Combine(_root, "workspace-real");
        Directory.CreateDirectory(Path.Combine(real, "src"));

        var throughLink = Path.Combine(_root, "workspace-link");
        CreateDirectoryLinkOrSkip(throughLink, real);

        WorkspacePath.Resolve(throughLink, "src/Class1.cs").Value.Should().Be(Path.Combine(real, "src", "Class1.cs"));
    }

    [SkippableTheory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("com1.log")]
    [InlineData("LPT1")]
    [InlineData("src/CON")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CLOCK$")]
    [InlineData("COM\u00B9")]
    [InlineData("COM\u00B2")]
    [InlineData("COM\u00B3")]
    [InlineData("LPT\u00B9")]
    [InlineData("CON .txt")]
    public void Refuses_reserved_device_names(string path)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "reserved device names are a Windows-only concept");

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [SkippableTheory]
    [InlineData("trailing-dot.")]
    [InlineData("trailing-space ")]
    [InlineData("src/trailing-dot.../x.cs")]
    public void Refuses_segments_with_a_trailing_dot_or_space(string path)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows silently trims trailing dots/spaces from a segment, which this guards against");

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [SkippableTheory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    public void Refuses_unc_paths(string path)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "backslash-form UNC syntax is a Windows concept");

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [Fact]
    public void Refuses_a_drive_relative_path() =>
        // "C:foo" (no separator after the colon) contains ':', which every OS now refuses, so this needs no
        // platform guard — it also exercises the drive-relative case specifically on Windows.
        WorkspacePath.Resolve(_workspace, "C:foo").IsFailure.Should().BeTrue();

    [Fact]
    public void Refuses_an_alternate_data_stream() =>
        // NTFS alternate-data-stream syntax contains ':', which every OS now refuses unconditionally.
        WorkspacePath.Resolve(_workspace, "src/file.txt:hidden-stream").IsFailure.Should().BeTrue();

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"mklink /J failed: {process.StandardError.ReadToEnd()}");
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }

    /// <summary>
    /// Creates a directory junction (Windows) or symlink (elsewhere), or reports the test as Skipped — never
    /// Passed — when it cannot. Directory junctions and Unix symlinks need no special privilege, so a failure here
    /// is unexpected; under <c>CI</c>, where this platform is expected to support it, that failure fails the test
    /// instead of skipping it, so CI can never pass vacuously.
    /// </summary>
    private static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            CreateDirectoryLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            FailOrSkip("create a directory link (junction or symlink)", ex);
        }
    }

    /// <summary>
    /// Creates a file symlink, or reports the test as Skipped — never Passed — when it cannot. Windows without
    /// Developer Mode or elevation refuses <see cref="File.CreateSymbolicLink(string, string)"/> for an
    /// unprivileged process; under <c>CI</c> (the Windows runner is admin, and Linux needs no privilege at all),
    /// that failure fails the test instead of skipping it, so CI can never pass vacuously. This exercises a file
    /// symlink specifically — the junction-based tests above only exercise a directory target.
    /// </summary>
    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException ex)
        {
            FailOrSkip("create a file symlink", ex);
        }
    }

    /// <summary>
    /// Creates a real directory symlink — never a junction, even on Windows — or reports the test as Skipped. A
    /// relative target, a target containing <c>".."</c>, a loop, and a self-reference cannot be represented by a
    /// junction at all: a junction requires an existing target at creation time and always records an absolute NT
    /// path, never a relative or <c>".."</c>-bearing one. These tests accept the same privilege dependency as
    /// <see cref="CreateFileSymlinkOrSkip"/> on an unprivileged Windows process.
    /// </summary>
    private static void CreateRealSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException ex)
        {
            FailOrSkip("create a directory symlink", ex);
        }
    }

    /// <summary>
    /// Creates a junction (Windows) or symlink (elsewhere) at <paramref name="linkPath"/> whose target has since
    /// been removed, without needing symlink privilege on Windows: a junction requires an existing target at
    /// creation time, so a real one is created, linked to, and then deleted, leaving the link dangling.
    /// </summary>
    private static void CreateDanglingDirectoryLinkOrSkip(string linkPath)
    {
        var target = linkPath + "-target";
        Directory.CreateDirectory(target);
        CreateDirectoryLinkOrSkip(linkPath, target);
        Directory.Delete(target);
    }

    private static void FailOrSkip(string action, Exception? ex)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null)
            throw new InvalidOperationException($"Could not {action} under CI, where this platform is expected to support it.", ex);

        Skip.If(true, $"Could not {action} on this machine{(ex is null ? "" : $": {ex.Message}")}.");
    }
}
