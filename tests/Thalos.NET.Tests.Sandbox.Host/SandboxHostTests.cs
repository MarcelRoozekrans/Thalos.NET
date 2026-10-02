using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Sandbox.Host;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox.Host;

/// <summary>
/// A8: the in-container host, run in-process. The bundle is made by <see cref="GitMirrorStore"/> from a real git
/// remote; restore and build go to a counting fake runner; the Roslyn server is the stdio test server, started by the
/// host's own registry with a <c>list_solutions</c> ready tool and no reload.
/// </summary>
public sealed class SandboxHostTests : IDisposable
{
    private static readonly string[] WorkspaceTools = ["read_file", "list_files", "write_file", "edit_file"];
    private static readonly string[] BuildTools = ["build", "test"];

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-sbx-host-").FullName;
    private readonly List<LocalGitRemote> _remotes = [];

    private string WorkRoot(string name = "work") => Path.Combine(_temp, name);

    private string MirrorData => Path.Combine(_temp, "mirror-data");

    private LocalGitRemote Remote()
    {
        var remote = LocalGitRemote.Create(("App.slnx", "<Solution />\n"), ("A.cs", "class A { }\n"));
        _remotes.Add(remote);
        return remote;
    }

    /// <summary>
    /// Red: in SandboxHost.Map, skip BearerTokenMiddleware for paths under /mcp. The /mcp rows then reach the MCP
    /// endpoint and answer 400 or 405 instead of 401.
    /// </summary>
    [Fact]
    public async Task Every_route_requires_the_bearer_token()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Post, $"/control/import?branch=b&commit={new string('a', 40)}"),
            (HttpMethod.Get, "/control/ready"),
            (HttpMethod.Post, "/control/export"),
            (HttpMethod.Post, "/mcp/workspace"),
            (HttpMethod.Post, "/mcp/sandbox"),
            (HttpMethod.Post, "/mcp/roslyn"),
            (HttpMethod.Get, "/mcp/workspace"),
            (HttpMethod.Get, "/nowhere"),
        ];
        string?[] wrong =
        [
            null,
            "Bearer wrong",
            "Bearer " + HostHarness.Token[..^1] + "x", // same length, last character differs
            "Bearer " + HostHarness.Token + "x",
            HostHarness.Token, // no scheme
            "bearer " + HostHarness.Token, // the scheme is matched exactly
        ];

        foreach (var (method, path) in routes)
        {
            foreach (var authorization in wrong)
            {
                using var response = await host.SendAsync(method, path, authorization);
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} with '{authorization}'");
                (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            }
        }

        using var authorized = await host.SendAsync(HttpMethod.Get, "/control/ready");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK, "the right token is let through");
    }

    /// <summary>
    /// Red: in ImportService.CheckOutAsync, run `git checkout -b branch` without the commit. The clone has no HEAD, so
    /// the branch is unborn: the checkout leaves no files and HEAD does not resolve to the commit.
    /// </summary>
    [Fact]
    public async Task Import_clones_the_bundle_at_the_commit_and_reports_ready()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var remote = Remote();

        var (commit, ready) = await host.ImportAndSettleAsync(remote, MirrorData, branch: "run/feature");

        ready.Should().Be(new ReadyBody(Imported: true, "ok", "Restore complete.\n", "ready", Detail: null));
        LocalGitRemote.RunGit(host.RepoRoot, "rev-parse", "HEAD").Should().Be(commit);
        LocalGitRemote.RunGit(host.RepoRoot, "symbolic-ref", "--short", "HEAD").Should().Be("run/feature");
        File.ReadAllText(Path.Combine(host.RepoRoot, "A.cs")).Should().Be("class A { }\n");
        LocalGitRemote.RunGit(host.RepoRoot, "config", "--get", "core.symlinks").Should().Be("false", "the clone itself checks out symlinks as plain files");

        var restore = host.Runner.Restores.Should().ContainSingle().Subject;
        var solution = WorkspacePath.Resolve(host.RepoRoot, "App.slnx").Value;
        restore.FileName.Should().Be("dotnet");
        restore.Arguments.Should().Equal("restore", solution, "--nologo", "-v:q");
        restore.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        Directory.EnumerateFiles(Path.Combine(host.WorkRoot, "import")).Should().BeEmpty("the bundle is deleted after the clone");
    }

    /// <summary>Red: in ImportService.StartAsync, drop the CompareExchange claim, so a second import is accepted.</summary>
    [Fact]
    public async Task A_second_import_is_refused()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var (bundle, commit) = await HostHarness.BundleAsync(Remote(), MirrorData);

        using var first = await host.ImportAsync(bundle, "run/one", commit);
        using var second = await host.ImportAsync(bundle, "run/two", commit);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await host.WaitSettledAsync()).Roslyn.Should().Be("ready");
        LocalGitRemote.RunGit(host.RepoRoot, "symbolic-ref", "--short", "HEAD").Should().Be("run/one", "the refused import changed nothing");
    }

    /// <summary>
    /// Red 1: drop the check-ref-format call from ImportService.IsValidBranchAsync; "a..b" is then accepted.
    /// Red 2: drop the ".." check from IsAcceptableSolution; "../x.slnx" is then accepted.
    /// Red 3: keep the claim when a request is refused; the valid import after the refusals is then 409.
    /// </summary>
    [Fact]
    public async Task An_invalid_branch_commit_or_solution_is_refused_before_anything_is_stored()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var (bundle, commit) = await HostHarness.BundleAsync(Remote(), MirrorData);

        (string Branch, string Commit, string? Solution)[] invalid =
        [
            ("a..b", commit, "App.slnx"),
            ("-x", commit, "App.slnx"),
            ("run/x", "main", "App.slnx"),
            ("run/x", commit[..39], "App.slnx"),
            ("run/x", commit, "../x.slnx"),
            ("run/x", commit, Path.Combine(_temp, "x.slnx")),
        ];
        foreach (var (branch, sha, solution) in invalid)
        {
            using var refused = await host.ImportAsync(bundle, branch, sha, solution);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"branch '{branch}', commit '{sha}', solution '{solution}'");
        }

        Directory.EnumerateFileSystemEntries(host.RepoRoot).Should().BeEmpty();
        using var accepted = await host.ImportAsync(bundle, "run/x", commit);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, "a refused request does not use up the import");
    }

    /// <summary>
    /// Red 1: in the ConfigureSessionOptions callback, add every route's tools to every route. Red 2: drop the
    /// `options.ToolCollection = []` reset and add to the collection as it arrives; the container-registered `leak`
    /// tool then appears on every route.
    /// </summary>
    [Fact]
    public async Task Each_route_serves_only_its_own_tools()
    {
        await using var host = await HostHarness.StartAsync(
            WorkRoot(),
            services: s => s.AddSingleton(McpServerTool.Create(() => "leaked", new McpServerToolCreateOptions { Name = "leak" })));
        await host.ImportAndSettleAsync(Remote(), MirrorData);

        var workspace = await ToolNamesAsync(host, "workspace");
        var sandbox = await ToolNamesAsync(host, "sandbox");
        var roslyn = await ToolNamesAsync(host, "roslyn");

        workspace.Should().BeEquivalentTo(WorkspaceTools);
        sandbox.Should().BeEquivalentTo(BuildTools);
        roslyn.Should().Contain(["echo", "env", "list_solutions"]).And.NotContain([.. WorkspaceTools, .. BuildTools, "leak"]);

        await using var client = await host.ConnectAsync("workspace");
        var foreign = async () => await client.CallToolAsync("build");
        await foreign.Should().ThrowAsync<McpException>("a tool of another route is unknown here");
    }

    /// <summary>
    /// Red 1: under '*', register UseRunWorkspaceTools with an empty extension set; the csproj write is then refused.
    /// Red 2: skip AddProtected in configure; the .github write then succeeds.
    /// Red 3: do not begin the TurnScope in SandboxScopedTool; every call then answers that the turn has no run workspace.
    /// </summary>
    [Fact]
    public async Task A_csproj_write_is_allowed_under_star_and_a_github_write_is_refused()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);

        var csproj = await host.CallAsync("workspace", "write_file", ("path", "App.csproj"), ("content", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n"));
        var github = await host.CallAsync("workspace", "write_file", ("path", ".github/workflows/evil.yml"), ("content", "on: push\n"));

        csproj.Should().Match("wrote * bytes to 'App.csproj'.");
        File.ReadAllText(Path.Combine(host.RepoRoot, "App.csproj")).Should().Be("<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        github.Should().Be("error: '.github/workflows/evil.yml' is protected and cannot be written.");
        File.Exists(Path.Combine(host.RepoRoot, ".github", "workflows", "evil.yml")).Should().BeFalse();
    }

    /// <summary>Red: register UseRunWorkspaceToolsAllowingAnyExtension whatever the setting; the csproj write then succeeds.</summary>
    [Fact]
    public async Task Under_an_extension_list_a_csproj_write_is_refused()
    {
        await using var host = await HostHarness.StartAsync(
            WorkRoot(), new Dictionary<string, string>(StringComparer.Ordinal) { [SandboxEnvironment.WriteExtensions] = ".cs;.md" });
        await host.ImportAndSettleAsync(Remote(), MirrorData);

        var cs = await host.CallAsync("workspace", "write_file", ("path", "B.cs"), ("content", "class B { }\n"));
        var csproj = await host.CallAsync("workspace", "write_file", ("path", "App.csproj"), ("content", "<Project />\n"));

        cs.Should().Match("wrote * bytes to 'B.cs'.");
        csproj.Should().StartWith("error: extension '.csproj' is not writable");
        File.Exists(Path.Combine(host.RepoRoot, "App.csproj")).Should().BeFalse();
    }

    /// <summary>
    /// Red: skip `git add -A`; the patch is then empty. Dropping --cached is not a red here: straight after `git add -A`
    /// the worktree equals the index, so both diffs are the same. --cached keeps the patch to what was staged when a
    /// write lands between the two commands, a race no deterministic test reproduces.
    /// </summary>
    [Fact]
    public async Task Export_returns_a_patch_of_new_changed_and_deleted_files()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        using (var early = await host.SendAsync(HttpMethod.Post, "/control/export"))
        {
            early.StatusCode.Should().Be(HttpStatusCode.Conflict, "nothing has been imported");
        }

        var remote = Remote();
        await host.ImportAndSettleAsync(remote, MirrorData);
        (await ExportAsync(host)).Should().BeEmpty("no change yet");

        (await host.CallAsync("workspace", "write_file", ("path", "New.cs"), ("content", "class New { }\n"))).Should().Match("wrote * bytes to 'New.cs'.");
        (await host.CallAsync("workspace", "edit_file", ("path", "A.cs"), ("oldText", "class A"), ("newText", "class Changed"))).Should().Be("edited 'A.cs'.");
        File.Delete(Path.Combine(host.RepoRoot, "README.md")); // no tool deletes; a build could

        var trusted = Directory.CreateDirectory(Path.Combine(_temp, "trusted")).FullName;
        var patch = Path.Combine(trusted, "run.patch");
        await File.WriteAllBytesAsync(patch, await ExportAsync(host));
        var publishData = Path.Combine(_temp, "publish");
        var provider = new GitWorktreeWorkspaceProvider(new GitWorkspaceOptions { DataRoot = publishData }, [], NullLogger<GitWorktreeWorkspaceProvider>.Instance, TimeProvider.System);
        var fresh = await provider.CreateAsync(new RunWorkspaceRequest(Guid.NewGuid(), "repo", remote.Url, "main", "publish/run", null), CancellationToken.None);
        fresh.IsSuccess.Should().BeTrue(fresh.IsFailure ? fresh.Error.Message : "");

        var applied = await new GitPatchApplier(new GitWorkspaceOptions { DataRoot = publishData }, NullLogger<GitPatchApplier>.Instance)
            .ApplyAsync(fresh.Value, patch, new ProtectedPathSet([".git/", "AGENT.md", ".github/"]), new PatchApplyLimits(), CancellationToken.None);

        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.Message + " " + applied.Error.Detail : "");
        applied.Value.Should().BeEquivalentTo(["New.cs", "A.cs", "README.md"]);
        File.ReadAllText(Path.Combine(fresh.Value.Root, "New.cs")).Should().Be("class New { }\n");
        File.ReadAllText(Path.Combine(fresh.Value.Root, "A.cs")).Should().Be("class Changed { }\n");
        File.Exists(Path.Combine(fresh.Value.Root, "README.md")).Should().BeFalse();
        Directory.EnumerateFiles(Path.Combine(host.WorkRoot, "export")).Should().BeEmpty("the patch file is deleted once sent");
    }

    /// <summary>
    /// Red: make RestoreService.OnFilesChanged return without marking restore dirty; the count then stays at 1 after
    /// the csproj write.
    /// </summary>
    [Fact]
    public async Task A_build_file_change_reruns_restore_before_the_next_roslyn_call()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        host.Runner.Restores.Should().HaveCount(1);

        await host.CallAsync("workspace", "write_file", ("path", "App.csproj"), ("content", "<Project />\n"));
        host.Runner.Restores.Should().HaveCount(1, "the write only marks restore dirty");
        (await host.CallAsync("roslyn", "echo", ("text", "hi"))).Should().Be("echo:hi");
        host.Runner.Restores.Should().HaveCount(2, "the csproj write is restored before the Roslyn call");

        await host.CallAsync("workspace", "write_file", ("path", "B.cs"), ("content", "class B { }\n"));
        await host.CallAsync("roslyn", "echo", ("text", "again"));
        host.Runner.Restores.Should().HaveCount(2, "a .cs write changes nothing restore resolves");

        await host.CallAsync("workspace", "write_file", ("path", "Directory.Build.props"), ("content", "<Project />\n"));
        (await host.CallAsync("sandbox", "build")).Should().StartWith("exit: 0");
        host.Runner.Restores.Should().HaveCount(3, "a build call is restored first too");
    }

    /// <summary>
    /// Red: in RoslynProxyTools.CallAsync, pass only the caller's token to CallToolAsync; the slow call then runs its
    /// full five seconds and answers "slow done".
    /// </summary>
    [Fact]
    public async Task A_roslyn_call_is_cut_off_at_the_call_timeout()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        host.Services.GetRequiredService<RoslynProxyTools>().CallTimeout = TimeSpan.FromMilliseconds(500);

        var answer = await host.CallAsync("roslyn", "slow", ("ms", 5000));

        answer.Should().StartWith("error: the Roslyn server did not answer 'slow' within");
        (await host.CallAsync("roslyn", "echo", ("text", "after"))).Should().Be("echo:after", "the timed-out call released its lease");
    }

    /// <summary>
    /// Red: in ImportService.RunAsync, publish the workspace ready only when the restore succeeded; Roslyn then stays
    /// pending and the run never becomes ready.
    /// </summary>
    [Fact]
    public async Task A_failed_restore_still_makes_the_run_ready_and_says_why()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        host.Runner.RestoreOutcome = new ProcessOutcome(1, TimedOut: false, "error NU1101: Unable to find package Nope.\n");

        var (_, ready) = await host.ImportAndSettleAsync(Remote(), MirrorData);

        ready.Imported.Should().BeTrue();
        ready.Restore.Should().Be("failed");
        ready.RestoreDetail.Should().Contain("exited 1").And.Contain("NU1101");
        ready.Roslyn.Should().Be("ready");
    }

    /// <summary>
    /// Red: add SandboxEnvironment.Token to the Roslyn definition's PassEnvironment; the server then sees the token
    /// this test puts in the host process's environment.
    /// </summary>
    [Fact]
    public async Task The_roslyn_server_never_receives_the_sandbox_token()
    {
        var previous = Environment.GetEnvironmentVariable(SandboxEnvironment.Token);
        Environment.SetEnvironmentVariable(SandboxEnvironment.Token, HostHarness.Token);
        try
        {
            await using var host = await HostHarness.StartAsync(WorkRoot());
            await host.ImportAndSettleAsync(Remote(), MirrorData);

            (await host.CallAsync("roslyn", "env", ("name", SandboxEnvironment.Token))).Should().Be("<unset>");
            (await host.CallAsync("roslyn", "env", ("name", "ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"))).Should().Be("600", "the server's own environment does arrive");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SandboxEnvironment.Token, previous);
        }
    }

    /// <summary>Red: skip CreateWorkDirectories in SandboxHost.CreateBuilder.</summary>
    [Fact]
    public async Task Startup_creates_the_work_directories_the_volume_hides()
    {
        var root = WorkRoot("fresh-volume");

        await using var host = await HostHarness.StartAsync(root);

        Directory.Exists(Path.Combine(root, "home")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "nuget")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "repo")).Should().BeTrue();
    }

    /// <summary>Red: in SandboxSettings.Read, drop the token length check; the short token then starts a host.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task The_host_refuses_to_start_without_a_strong_token(string? token)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { [SandboxEnvironment.Token] = token ?? "" };

        var start = async () => await HostHarness.StartAsync(WorkRoot(), settings);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{SandboxEnvironment.Token}*");
    }

    /// <summary>Red: in ImportService.StoreAsync, write every chunk without comparing the running total to the cap.</summary>
    [Fact]
    public async Task A_bundle_over_the_cap_is_refused_while_streaming()
    {
        var atCap = Path.Combine(_temp, "at-cap");
        var overCap = Path.Combine(_temp, "over-cap");

        (await ImportService.StoreAsync(new MemoryStream(new byte[1000]), atCap, 1000, CancellationToken.None)).Should().BeTrue();
        (await ImportService.StoreAsync(new MemoryStream(new byte[1001]), overCap, 1000, CancellationToken.None)).Should().BeFalse();
        new FileInfo(atCap).Length.Should().Be(1000);
    }

    public void Dispose()
    {
        foreach (var remote in _remotes)
        {
            remote.Dispose();
        }

        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_temp, recursive: true);
    }

    private static async Task<string[]> ToolNamesAsync(HostHarness host, string source)
    {
        await using var client = await host.ConnectAsync(source);
        return [.. (await client.ListToolsAsync()).Select(t => t.Name)];
    }

    private static async Task<byte[]> ExportAsync(HostHarness host)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/control/export");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        return await response.Content.ReadAsByteArrayAsync();
    }
}
