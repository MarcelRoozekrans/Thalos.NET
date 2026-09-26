using Thalos.Tests.Unit.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class WorkspaceToolsTests
{
    private static readonly Guid RunId = Guid.NewGuid();

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

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(".git/config")]
    public async Task Escapes_are_refused_by_the_tool(string path) =>
        (await Build().Tools.WriteFile(Caller(RunId), path, "x")).Should().StartWith("error:");

    /// <summary>
    /// The same two escapes as <see cref="Escapes_are_refused_by_the_tool"/>, but against targets that already
    /// exist with an allow-listed extension, so an unconfined write would actually succeed rather than merely fail
    /// with "does not exist" — that distinction matters because the plain refusal above can't tell a genuine
    /// confinement refusal from a target that simply isn't there.
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

    [Fact]
    public async Task A_link_named_like_an_allowed_file_cannot_smuggle_a_disallowed_one()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.CreateSymbolicLink(Path.Combine(root, "alias.cs"), Path.Combine(root, "Directory.Build.props"));

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

    [Fact]
    public async Task Read_file_refuses_a_file_over_the_configured_size_limit()
    {
        var (tools, _, root) = Build(maxReadBytes: 4);
        File.WriteAllText(Path.Combine(root, "big.md"), "more than four bytes");

        (await tools.ReadFile(Caller(RunId), "big.md")).Should().StartWith("error:");
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
    /// via the internal <see cref="WorkspaceTools.BeforeOpenForTesting"/> seam, so the race is deterministic rather
    /// than timing-dependent. Unfixed, the open would follow the swapped link and edit the outside file in place.
    /// </summary>
    [SkippableFact]
    public async Task A_directory_swapped_for_a_link_between_resolve_and_open_is_refused_without_touching_the_outside_file()
    {
        var (tools, _, root) = Build();
        var outside = Directory.CreateTempSubdirectory("thalos-workspace-tools-outside-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "escape.cs"), "outside-original");
            File.WriteAllText(Path.Combine(outside, "escape.cs"), "outside-original");

            WorkspaceTools.BeforeOpenForTesting = _ =>
            {
                Directory.Delete(Path.Combine(root, "sub"), recursive: true);
                CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
            };

            var result = await tools.EditFile(Caller(RunId), "sub/escape.cs", "outside-original", "malicious");

            result.Should().Contain("not permitted");
            File.ReadAllText(Path.Combine(outside, "escape.cs")).Should().Be("outside-original");
        }
        finally
        {
            WorkspaceTools.BeforeOpenForTesting = null;
        }
    }

    private static (WorkspaceTools Tools, FakeChangeListener Listener, string Root) Build(
        IReadOnlySet<string>? allowedWriteExtensions = null,
        IEnumerable<string>? protectedPaths = null,
        int? maxReadBytes = null,
        int? maxListEntries = null)
    {
        var root = Directory.CreateTempSubdirectory("thalos-workspace-tools-").FullName;
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

        var listener = new FakeChangeListener();
        var tools = new WorkspaceTools(provider, options, [listener]);
        return (tools, listener, root);
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
            FailOrSkip("create a file symlink", ex);
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
                FailOrSkip("create a directory junction", new InvalidOperationException(process.StandardError.ReadToEnd()));
            }

            return;
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FailOrSkip("create a directory link", ex);
        }
    }

    private static void FailOrSkip(string action, Exception? ex)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null)
        {
            throw new InvalidOperationException($"Could not {action} under CI, where this platform is expected to support it.", ex);
        }

        Skip.If(true, $"Could not {action} on this machine{(ex is null ? "" : $": {ex.Message}")}.");
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
}
