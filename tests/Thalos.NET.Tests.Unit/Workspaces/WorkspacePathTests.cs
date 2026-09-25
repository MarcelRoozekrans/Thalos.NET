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
        // check — not the containment check on the collapsed path — can refuse it.
        WorkspacePath.Resolve(_workspace, "src/../src/Class1.cs").IsFailure.Should().BeTrue();

    [Fact]
    public void Refuses_a_sibling_directory_that_merely_shares_the_workspace_name_as_a_prefix()
    {
        // Caught by the ".." segment check before containment is even considered — kept as a regression test for
        // that check specifically. Refuses_a_link_target_that_is_a_sibling_sharing_the_workspace_name_as_a_prefix,
        // below, isolates the containment separator check on its own, through a route the ".." check cannot reach.
        Directory.CreateDirectory(Path.Combine(_root, "workspace-evil"));

        WorkspacePath.Resolve(_workspace, "../workspace-evil/x").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_link_target_that_is_a_sibling_sharing_the_workspace_name_as_a_prefix()
    {
        // A link target reaches the containment check directly, with no relativePath ".." segment involved, so
        // this is the one case a StartsWith(root) without the trailing separator would wrongly accept: the target
        // "…\workspace-evil" textually starts with "…\workspace".
        var evilSibling = Path.Combine(_root, "workspace-evil");
        Directory.CreateDirectory(evilSibling);
        CreateDirectoryLink(Path.Combine(_workspace, "escape-evil"), evilSibling);

        WorkspacePath.Resolve(_workspace, "escape-evil/x").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_directory_link_pointing_out()
    {
        CreateDirectoryLink(Path.Combine(_workspace, "escape"), _outside);

        WorkspacePath.Resolve(_workspace, "escape/secret.txt").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_file_symlink_pointing_out()
    {
        if (!TryCreateFileSymlink(Path.Combine(_workspace, "link.txt"), Path.Combine(_outside, "secret.txt")))
            return; // recorded in the task report: no privilege to create file symlinks on this machine

        WorkspacePath.Resolve(_workspace, "link.txt").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Allows_a_link_that_stays_inside()
    {
        CreateDirectoryLink(Path.Combine(_workspace, "alias"), Path.Combine(_workspace, "src"));

        WorkspacePath.Resolve(_workspace, "alias/x.cs").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Allows_a_normal_nested_path_when_the_workspace_root_is_itself_reached_through_a_junction()
    {
        var real = Path.Combine(_root, "workspace-real");
        Directory.CreateDirectory(Path.Combine(real, "src"));

        var throughLink = Path.Combine(_root, "workspace-link");
        CreateDirectoryLink(throughLink, real);

        WorkspacePath.Resolve(throughLink, "src/Class1.cs").Value.Should().Be(Path.Combine(real, "src", "Class1.cs"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("com1.log")]
    [InlineData("LPT1")]
    [InlineData("src/CON")]
    public void Refuses_reserved_device_names(string path)
    {
        if (!OperatingSystem.IsWindows())
            return; // reserved device names are a Windows-only concept

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [Theory]
    [InlineData("trailing-dot.")]
    [InlineData("trailing-space ")]
    [InlineData("src/trailing-dot.../x.cs")]
    public void Refuses_segments_with_a_trailing_dot_or_space(string path)
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows silently trims trailing dots/spaces from a segment, which this guards against

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    public void Refuses_unc_paths(string path)
    {
        if (!OperatingSystem.IsWindows())
            return; // UNC paths are a Windows concept; IsPathRooted does not recognise "\\" on other platforms

        WorkspacePath.Resolve(_workspace, path).IsFailure.Should().BeTrue(path);
    }

    [Fact]
    public void Refuses_a_drive_relative_path()
    {
        if (!OperatingSystem.IsWindows())
            return; // "C:foo" (no separator after the colon) is only meaningful on Windows

        WorkspacePath.Resolve(_workspace, "C:foo").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Refuses_an_alternate_data_stream()
    {
        if (!OperatingSystem.IsWindows())
            return; // NTFS alternate data streams are a Windows concept

        WorkspacePath.Resolve(_workspace, "src/file.txt:hidden-stream").IsFailure.Should().BeTrue();
    }

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

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            // Windows without Developer Mode enabled refuses File.CreateSymbolicLink for an unprivileged process;
            // CI's Windows runner (admin) exercises this test for real. Do not add a skip attribute for this.
            return false;
        }
    }
}
