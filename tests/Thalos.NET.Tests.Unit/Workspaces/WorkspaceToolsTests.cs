using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Tests.Unit.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class WorkspaceToolsTests : IDisposable
{
    private static readonly Guid RunId = Guid.NewGuid();
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task Write_then_read_round_trips_inside_the_run_workspace_and_notifies_listeners()
    {
        var (tools, listener, root) = Build();

        (await tools.WriteFile(Caller(RunId), "src/New.cs", "class N {}")).Should().StartWith("wrote");
        File.ReadAllText(Path.Combine(root, "src", "New.cs")).Should().Be("class N {}");
        (await tools.ReadFile(Caller(RunId), "src/New.cs")).Should().Be("class N {}");
        listener.Changes.Should().ContainSingle().Which.Should().Be((RunId, "src/New.cs"));
    }

    [Fact]
    public async Task A_caller_without_a_run_claim_touches_nothing() =>
        (await Build().Tools.WriteFile(new TestSecurityContext("chat-user"), "x.txt", "y")).Should().StartWith("error: this turn has no run workspace");

    /// <summary>
    /// Against targets that already exist with an allow-listed extension, so an unconfined write would actually
    /// succeed rather than merely fail with "does not exist" — a plain refusal on a target that isn't there can't
    /// tell a genuine confinement refusal from one that never had anything to reach.
    /// </summary>
    [Fact]
    public async Task Escapes_are_refused_even_when_the_outside_and_git_targets_already_exist()
    {
        var (tools, _, root) = Build();
        var outsideFile = Path.Combine(Path.GetDirectoryName(root)!, $"escape-{RunId:N}.cs");
        File.WriteAllText(outsideFile, "untouched");
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        try
        {
            (await tools.WriteFile(Caller(RunId), "../" + Path.GetFileName(outsideFile), "hijack")).Should().StartWith("error:");
            (await tools.WriteFile(Caller(RunId), ".git/x.cs", "hijack")).Should().StartWith("error:");

            File.ReadAllText(outsideFile).Should().Be("untouched");
            File.Exists(Path.Combine(root, ".git", "x.cs")).Should().BeFalse();
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task A_protected_path_is_readable_but_not_writable()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");

        (await tools.ReadFile(Caller(RunId), "AGENT.md")).Should().Be("pinned");
        (await tools.WriteFile(Caller(RunId), "agent.MD", "hijack")).Should().Contain("protected");
        (await tools.EditFile(Caller(RunId), "AGENT.md", "pinned", "x")).Should().Contain("protected");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
    }

    [SkippableFact]
    public async Task A_link_to_a_protected_path_is_also_protected()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");
        CreateFileSymlinkOrSkip(Path.Combine(root, "alias.md"), Path.Combine(root, "AGENT.md"));

        (await tools.WriteFile(Caller(RunId), "alias.md", "hijack")).Should().Contain("protected");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
    }

    /// <summary>
    /// The swap the review found: <c>sub</c> is a real directory containing no <c>AGENT.md</c> of its own, so
    /// <c>WorkspacePath.Resolve("sub/AGENT.md")</c> resolves cleanly and the pre-check — comparing
    /// <c>"sub/AGENT.md"</c> against the protected path <c>"AGENT.md"</c> — correctly sees no match. Between that
    /// resolve and the tool's own open, <c>sub</c> is swapped for a junction pointing at the workspace root itself,
    /// via the <see cref="WorkspaceTools.BeforeOpenForTesting"/> seam; the open then really reaches
    /// <c>root/AGENT.md</c>. Only the post-check, run against the handle's real path, catches this.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_through_a_subdirectory_does_not_bypass_agent_md_protection()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"));
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), root);
        };

        (await tools.WriteFile(Caller(RunId), "sub/AGENT.md", "hijack")).Should().Contain("protected");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
    }

    /// <summary>
    /// The review's second finding: <c>sub</c> is a real, empty directory; between resolve and open it is swapped
    /// for a link to a directory outside the workspace, so <c>write_file("sub/new.cs")</c>'s <c>FileMode.CreateNew</c>
    /// fallback actually creates <c>outside/new.cs</c> before the post-check can run. The post-check must still
    /// refuse the write, and — because this call created that file — must also remove it, by its real path, so
    /// nothing is left outside once the write is refused.
    /// </summary>
    [SkippableFact]
    public async Task A_write_through_a_swap_to_outside_leaves_nothing_behind()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"));
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
        };

        var result = await tools.WriteFile(Caller(RunId), "sub/new.cs", "hijack");

        result.Should().Contain("not permitted");
        File.Exists(Path.Combine(outside, "new.cs")).Should().BeFalse();
    }

    /// <summary>
    /// The review's third finding: a disallowed-extension write must leave no trace, including the parent
    /// directories it created to get there. <c>d</c> and <c>d/e</c> do not exist beforehand.
    /// </summary>
    [Fact]
    public async Task A_disallowed_extension_write_leaves_no_directories_behind()
    {
        var (tools, _, root) = Build();

        var result = await tools.WriteFile(Caller(RunId), "d/e/Makefile", "x");

        result.Should().Contain("extension ''");
        Directory.Exists(Path.Combine(root, "d", "e")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "d")).Should().BeFalse();
    }

    [Fact]
    public async Task Edit_requires_exactly_one_match()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "a.cs"), "x x");

        (await tools.EditFile(Caller(RunId), "a.cs", "x", "y")).Should().Contain("2");
        (await tools.EditFile(Caller(RunId), "a.cs", "z", "y")).Should().Contain("0");
        (await tools.EditFile(Caller(RunId), "a.cs", "x x", "y")).Should().StartWith("edited");
    }

    [Fact]
    public async Task List_files_skips_the_git_directory()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "x");

        (await tools.ListFiles(Caller(RunId))).Should().NotContain(".git");
    }

    // Ruling R29. Build() uses AllowedWriteExtensions = [".cs", ".md"] unless a test passes its own.
    [Theory]
    [InlineData("Directory.Build.props", false)]
    [InlineData("x.csproj", false)]
    [InlineData("x.targets", false)]
    [InlineData("Makefile", false)] // no extension
    [InlineData("x.CS", true)] // case-insensitive
    [InlineData("sub/x.cs", true)]
    public async Task Only_allow_listed_extensions_can_be_written(string path, bool allowed)
    {
        var (tools, _, root) = Build();
        var written = await tools.WriteFile(Caller(RunId), path, "content");

        written.StartsWith("wrote", StringComparison.Ordinal).Should().Be(allowed, written);
        File.Exists(Path.Combine(root, path)).Should().Be(allowed);
        if (!allowed)
        {
            written.Should().Contain($"extension '{Path.GetExtension(path)}'");
        }
    }

    [Fact]
    public async Task Edit_is_refused_for_a_disallowed_extension_and_leaves_the_file()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "x.csproj"), "<Project />");

        (await tools.EditFile(Caller(RunId), "x.csproj", "<Project />", "<Project><Target Name=\"X\" /></Project>")).Should().Contain("extension '.csproj'");
        File.ReadAllText(Path.Combine(root, "x.csproj")).Should().Be("<Project />");
    }

    [SkippableFact]
    public async Task A_link_named_like_an_allowed_file_cannot_smuggle_a_disallowed_one()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        CreateFileSymlinkOrSkip(Path.Combine(root, "alias.cs"), Path.Combine(root, "Directory.Build.props"));

        (await tools.WriteFile(Caller(RunId), "alias.cs", "<Project><Target Name=\"X\" /></Project>")).Should().Contain("extension '.props'");
        File.ReadAllText(Path.Combine(root, "Directory.Build.props")).Should().Be("<Project />");
    }

    [Fact]
    public async Task An_empty_allow_list_refuses_every_write()
    {
        var (tools, _, root) = Build(allowedWriteExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("error: extension '.cs'");
        File.Exists(Path.Combine(root, "a.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task A_callers_grant_claim_narrows_the_allow_list()
    {
        var (tools, _, _) = Build();
        var caller = Caller(RunId, writeExtensions: ".md"); // adds the RunWorkspaceClaims.WriteExtensions claim

        (await tools.WriteFile(caller, "a.cs", "class A {}")).Should().Contain("extension '.cs'");
        (await tools.WriteFile(caller, "notes.md", "x")).Should().StartWith("wrote");
    }

    /// <summary>A claim that is present but blank is a grant of zero extensions, not "no grant" — only an absent claim falls back to the ceiling.</summary>
    [Fact]
    public async Task A_blank_grant_claim_refuses_every_write()
    {
        var (tools, _, root) = Build();
        var caller = Caller(RunId, writeExtensions: " ");

        (await tools.WriteFile(caller, "a.cs", "class A {}")).Should().StartWith("error: extension '.cs'");
        File.Exists(Path.Combine(root, "a.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task Read_file_refuses_a_file_over_the_configured_size_limit()
    {
        var (tools, _, root) = Build(maxReadBytes: 4);
        File.WriteAllText(Path.Combine(root, "big.md"), "more than four bytes");

        (await tools.ReadFile(Caller(RunId), "big.md")).Should().StartWith("error:");
    }

    /// <summary>Unlike the old, unbounded <c>ReadToEndAsync</c>, <c>edit_file</c> now caps its read the same way <c>read_file</c> does.</summary>
    [Fact]
    public async Task Edit_file_refuses_a_file_over_the_configured_size_limit()
    {
        var (tools, _, root) = Build(maxReadBytes: 4);
        File.WriteAllText(Path.Combine(root, "big.md"), "more than four bytes");

        (await tools.EditFile(Caller(RunId), "big.md", "more", "less")).Should().StartWith("error:");
    }

    [Fact]
    public async Task List_files_caps_the_number_of_entries_at_the_configured_limit()
    {
        var (tools, _, root) = Build(maxListEntries: 2);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(root, $"f{i}.cs"), "x");
        }

        var listing = await tools.ListFiles(Caller(RunId));

        listing.Split('\n').Count(line => line.EndsWith(".cs", StringComparison.Ordinal)).Should().Be(2);
    }

    /// <summary>
    /// Simulates the check-to-use race the type-level remarks on <see cref="WorkspaceTools"/> describe: between
    /// <see cref="WorkspacePath.Resolve"/> succeeding for "sub/escape.cs" (a plain, real, in-workspace file at that
    /// moment) and the tool's own open, "sub" itself is swapped for a link to a directory outside the workspace —
    /// via the instance-level <see cref="WorkspaceTools.BeforeOpenForTesting"/> seam, so the race is deterministic
    /// rather than timing-dependent. Unfixed, the open would follow the swapped link and edit the outside file in
    /// place.
    /// </summary>
    [SkippableFact]
    public async Task A_directory_swapped_for_a_link_between_resolve_and_open_is_refused_without_touching_the_outside_file()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "escape.cs"), "outside-original");
        File.WriteAllText(Path.Combine(outside, "escape.cs"), "outside-original");

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"), recursive: true);
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
        };

        var result = await tools.EditFile(Caller(RunId), "sub/escape.cs", "outside-original", "malicious");

        result.Should().Contain("not permitted");
        File.ReadAllText(Path.Combine(outside, "escape.cs")).Should().Be("outside-original");
    }

    /// <summary>A listener that throws must never turn an otherwise-successful write into a failed tool call.</summary>
    [Fact]
    public async Task A_throwing_listener_still_leaves_the_write_successful()
    {
        var (tools, _, _) = Build(extraListener: static _ => new ThrowingChangeListener());

        var act = () => tools.WriteFile(Caller(RunId), "a.cs", "class A {}");

        (await act.Should().NotThrowAsync()).Which.Should().StartWith("wrote");
    }

    /// <summary>
    /// Notify must run only after the write's own handle is disposed — a listener that reads the file back through
    /// an ordinary, non-exclusive open must not collide with a still-open, <see cref="FileShare.None"/> handle.
    /// </summary>
    [Fact]
    public async Task A_reading_listener_can_read_the_file_the_write_just_produced()
    {
        ReadingChangeListener? reading = null;
        var (tools, _, _) = Build(extraListener: root =>
        {
            reading = new ReadingChangeListener(root);
            return reading;
        });

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("wrote");

        reading!.ReadBack.Should().Be("class A {}");
    }

    /// <summary>Listeners receive the changed path relative to the canonical root, with forward-slash separators, regardless of the OS.</summary>
    [Fact]
    public async Task A_listener_receives_a_root_relative_forward_slash_path()
    {
        var (tools, listener, _) = Build();

        (await tools.WriteFile(Caller(RunId), "sub/deep/File.cs", "x")).Should().StartWith("wrote");

        listener.Changes.Should().ContainSingle().Which.Path.Should().Be("sub/deep/File.cs");
    }

    /// <summary>
    /// A workspace root reached through a junction must work exactly like one reached directly: every comparison
    /// against the root must canonicalise it first, with the same kernel canonicalisation <see cref="WorkspacePath.Resolve"/>
    /// itself applies.
    /// </summary>
    [SkippableFact]
    public async Task A_root_reached_through_a_junction_works()
    {
        var real = NewTempDir("thalos-workspace-tools-real-");
        var linkParent = NewTempDir("thalos-workspace-tools-link-parent-");
        var throughLink = Path.Combine(linkParent, "root-link");
        CreateDirectoryLinkOrSkip(throughLink, real);

        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", throughLink, null);
        var provider = new FakeRunWorkspaceProvider(workspace);
        var options = new RunWorkspaceToolOptions { AllowedWriteExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" } };
        var tools = new WorkspaceTools(provider, options, [], NullLogger<WorkspaceTools>.Instance);

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("wrote");
        (await tools.ReadFile(Caller(RunId), "a.cs")).Should().Be("class A {}");
        File.ReadAllText(Path.Combine(real, "a.cs")).Should().Be("class A {}");
    }

    private (WorkspaceTools Tools, FakeChangeListener Listener, string Root) Build(
        IReadOnlySet<string>? allowedWriteExtensions = null,
        IEnumerable<string>? protectedPaths = null,
        int? maxReadBytes = null,
        int? maxListEntries = null,
        Func<string, IRunWorkspaceChangeListener>? extraListener = null)
    {
        var root = NewTempDir("thalos-workspace-tools-");
        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", root, null);

        var provider = new FakeRunWorkspaceProvider(workspace);

        var options = new RunWorkspaceToolOptions
        {
            AllowedWriteExtensions = allowedWriteExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md" },
        };
        if (maxReadBytes is { } bytes)
        {
            options.MaxReadBytes = bytes;
        }

        if (maxListEntries is { } entries)
        {
            options.MaxListEntries = entries;
        }

        if (protectedPaths is not null)
        {
            foreach (var protectedPath in protectedPaths)
            {
                options.ProtectedPaths.Add(protectedPath);
            }
        }

        var fake = new FakeChangeListener();
        var extra = extraListener?.Invoke(root);
        IEnumerable<IRunWorkspaceChangeListener> registered = extra is null ? [fake] : [fake, extra];
        var tools = new WorkspaceTools(provider, options, registered, NullLogger<WorkspaceTools>.Instance);
        return (tools, fake, root);
    }

    private string NewTempDir(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static TestSecurityContext Caller(Guid runId, string? writeExtensions = null)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString() };
        if (writeExtensions is not null)
        {
            claims[RunWorkspaceClaims.WriteExtensions] = writeExtensions;
        }

        return new TestSecurityContext("run-caller") { Claims = claims };
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException ex)
        {
            LinkTestHelpers.FailOrSkip("create a file symlink", ex);
        }
    }

    private static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
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
            {
                LinkTestHelpers.FailOrSkip("create a directory junction", new InvalidOperationException(process.StandardError.ReadToEnd()));
            }

            return;
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LinkTestHelpers.FailOrSkip("create a directory link", ex);
        }
    }

    /// <summary>Answers <see cref="FindAsync"/> for one known run with <paramref name="workspace"/>; every other member is unused by these tests.</summary>
    private sealed class FakeRunWorkspaceProvider(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) =>
            new(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");
    }

    private sealed class FakeChangeListener : IRunWorkspaceChangeListener
    {
        public List<(Guid RunId, string Path)> Changes { get; } = [];

        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths)
        {
            foreach (var path in relativePaths)
            {
                Changes.Add((runId, path));
            }
        }
    }

    private sealed class ThrowingChangeListener : IRunWorkspaceChangeListener
    {
        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths) => throw new InvalidOperationException("listener boom");
    }

    /// <summary>Reads the changed file back through an ordinary, non-exclusive open, to prove the write's own handle is already closed by the time listeners run.</summary>
    private sealed class ReadingChangeListener(string root) : IRunWorkspaceChangeListener
    {
        public string? ReadBack { get; private set; }

        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths) =>
            ReadBack = File.ReadAllText(Path.Combine(root, relativePaths[0]));
    }
}
