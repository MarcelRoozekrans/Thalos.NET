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

    [Fact]
    public void Refuses_everything_under_a_drive_root_workspace()
    {
        // A drive-root (or filesystem-root, on Linux) workspace is not a supported configuration, but it must fail
        // safe: refuse every path, rather than silently allowing access to the whole volume.
        var driveRoot = Path.GetPathRoot(_workspace)!;

        WorkspacePath.Resolve(driveRoot, "foo.txt").IsFailure.Should().BeTrue();
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
    /// symlink specifically — the junction-based tests above only exercise the directory branch of
    /// <c>WorkspacePath.LinkTargetOf</c>'s <see cref="File.Exists(string)"/> check.
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

    private static void FailOrSkip(string action, Exception? ex)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null)
            throw new InvalidOperationException($"Could not {action} under CI, where this platform is expected to support it.", ex);

        Skip.If(true, $"Could not {action} on this machine{(ex is null ? "" : $": {ex.Message}")}.");
    }
}
