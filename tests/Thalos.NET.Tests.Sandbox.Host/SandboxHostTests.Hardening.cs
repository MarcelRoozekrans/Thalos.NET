using System.Collections;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Sandbox.Host;
using Thalos.Tests.Git.Workspaces;

namespace Thalos.Tests.Sandbox.Host;

/// <summary>A8 fix round 1: child environments, the scratch copy, the export's exclusions and config guard, and the import's edges.</summary>
public sealed partial class SandboxHostTests
{
    /// <summary>
    /// The host's process environment holds the token in each of these tests, as it does in a container.
    /// Red 1 (restore): in RestoreService, pass the host's whole environment as the spec's Environment.
    /// Red 2 (build): in SandboxTools.Build, pass the host's whole environment instead of Curated().
    /// Red 3 (test): the same in SandboxTools.Test.
    /// Red 4 (all three): in SandboxProcessRunner.Start, skip Environment.Clear(); the token is then inherited.
    /// </summary>
    [Fact]
    public async Task Restore_build_and_test_start_without_the_sandbox_token()
    {
        using var token = new ProcessToken();
        var dumps = Directory.CreateDirectory(Path.Combine(_temp, "env")).FullName;
        var runner = new EnvDumpRunner(dumps);
        await using var host = await HostHarness.StartAsync(WorkRoot(), services: s => s.Replace(ServiceDescriptor.Singleton<ISandboxProcessRunner>(runner)));

        await host.ImportAndSettleAsync(Remote(), MirrorData);
        await host.CallAsync("sandbox", "build");
        await host.CallAsync("sandbox", "test");

        foreach (var verb in (string[])["restore", "build", "test"])
        {
            var names = EnvNames(runner.FileFor(verb));
            names.Should().NotContain(SandboxEnvironment.Token, $"{verb} must not see the token");
            names.Should().Contain(n => string.Equals(n, "PATH", StringComparison.OrdinalIgnoreCase), $"{verb} still gets the curated essentials");
        }
    }

    /// <summary>Red: in SandboxHost.AddServices, leave GitWorkspaceOptions.BaseEnvironment null, so git inherits the host's environment.</summary>
    [Fact]
    public async Task Git_starts_from_the_curated_environment()
    {
        using var token = new ProcessToken();
        await using var host = await HostHarness.StartAsync(WorkRoot());

        var options = host.Services.GetRequiredService<GitWorkspaceOptions>();

        options.BaseEnvironment.Should().NotBeNull();
        options.BaseEnvironment!.Keys.Should().NotContain(SandboxEnvironment.Token).And.Contain(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Red: leave SandboxToolOptions.ScratchRoot null in SandboxHost, so the test runs in /work/repo; its output file then
    /// lands in the worktree and the export.
    /// </summary>
    [Fact]
    public async Task A_test_run_writes_only_into_its_throwaway_copy()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        Directory.CreateDirectory(Path.Combine(host.RepoRoot, "obj")); // never copied
        string? ranIn = null;
        bool solutionThere = false, gitThere = true, objThere = true;
        host.Runner.OnRun = spec =>
        {
            if (spec.Arguments[0] is not "test")
            {
                return;
            }

            ranIn = spec.WorkingDirectory;
            solutionThere = File.Exists(spec.Arguments[1]) && spec.Arguments[1].StartsWith(spec.WorkingDirectory, StringComparison.Ordinal);
            gitThere = Directory.Exists(Path.Combine(spec.WorkingDirectory, ".git"));
            objThere = Directory.Exists(Path.Combine(spec.WorkingDirectory, "obj"));
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "TestResults.trx"), "<TestRun />");
        };

        (await host.CallAsync("sandbox", "test")).Should().StartWith("exit: 0");

        ranIn.Should().StartWith(Path.Combine(host.WorkRoot, "scratch"));
        solutionThere.Should().BeTrue("the solution is copied and the target points into the copy");
        gitThere.Should().BeFalse();
        objThere.Should().BeFalse();
        File.Exists(Path.Combine(host.RepoRoot, "TestResults.trx")).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(Path.Combine(host.WorkRoot, "scratch")).Should().BeEmpty("the copy is deleted afterwards");
        (await ExportAsync(host)).Should().BeEmpty("nothing the test wrote reaches the export");
    }

    /// <summary>Red: drop the bin and obj exclude pathspecs from ExportService's git add.</summary>
    [Fact]
    public async Task Export_leaves_out_bin_and_obj_at_any_depth_without_a_gitignore()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        File.Exists(Path.Combine(host.RepoRoot, ".gitignore")).Should().BeFalse("the repository has no .gitignore for this test to mean anything");
        foreach (var file in (string[])["obj/project.assets.json", "bin/Debug/App.dll", "Lib/obj/Lib.csproj.nuget.g.props", "Lib/bin/Release/Lib.dll", "Lib/Keep.cs"])
        {
            var path = Path.Combine(host.RepoRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x\n");
        }

        var patch = Encoding.UTF8.GetString(await ExportAsync(host));

        var files = patch.Split('\n').Where(l => l.StartsWith("diff --git ", StringComparison.Ordinal)).ToList();
        files.Should().ContainSingle().Which.Should().Be("diff --git a/Lib/Keep.cs b/Lib/Keep.cs");
    }

    /// <summary>
    /// Red: skip RepoConfigGuard.CheckAsync in ExportService; git add then runs the planted clean filter, which writes the
    /// marker.
    /// </summary>
    [Fact]
    public async Task Export_refuses_a_repository_whose_git_config_was_planted()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        var marker = Path.Combine(host.WorkRoot, "filter-ran.txt");
        LocalGitRemote.RunGit(host.RepoRoot, "config", "filter.x.clean", "echo ran > ../filter-ran.txt; cat");
        File.WriteAllText(Path.Combine(host.RepoRoot, ".gitattributes"), "* filter=x\n");

        using var response = await host.SendAsync(HttpMethod.Post, "/control/export");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("'filter.x.clean'");
        File.Exists(marker).Should().BeFalse("no git command ran over the planted config");
    }

    /// <summary>
    /// What clone writes, and GitCli's own -c flags, pass; a planted key, or any key from another scope, does not.
    /// Red 1: make RepoConfigGuard.IsAllowed accept every local key. Red 2: accept every scope.
    /// </summary>
    [Theory]
    [InlineData("local", "core.repositoryformatversion", true)]
    [InlineData("local", "remote.origin.fetch", true)]
    [InlineData("local", "branch.run/x.merge", true)]
    [InlineData("local", "filter.x.clean", false)]
    [InlineData("local", "include.path", false)]
    [InlineData("local", "core.fsmonitor", false)]
    [InlineData("local", "diff.external", false)]
    [InlineData("local", "extensions.worktreeconfig", false)]
    [InlineData("local", "core.hookspath", false)]
    [InlineData("command", "core.hookspath", true)]
    [InlineData("command", "protocol.file.allow", true)]
    [InlineData("command", "filter.x.clean", false)]
    [InlineData("global", "core.repositoryformatversion", false)]
    [InlineData("global", "filter.x.clean", false)]
    [InlineData("system", "core.bare", false)]
    [InlineData("worktree", "core.bare", false)]
    public void The_config_allow_list_holds_only_what_clone_and_GitCli_write(string scope, string key, bool allowed) =>
        RepoConfigGuard.IsAllowed(scope, key).Should().Be(allowed);

    /// <summary>
    /// GitCli's global config file lives on the work volume, writable by agent code like the repository's own config.
    /// Red: list only the local config in RepoConfigGuard.CheckAsync (add --local); git add then runs the planted
    /// global filter, which writes the marker.
    /// </summary>
    [Fact]
    public async Task Export_refuses_a_filter_planted_in_the_global_git_config()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        var marker = Path.Combine(host.WorkRoot, "filter-ran.txt");
        var global = Path.Combine(host.WorkRoot, "git", ".git-isolation", "global.config");
        File.Exists(global).Should().BeTrue("the planted file must be the one GitCli points GIT_CONFIG_GLOBAL at");
        File.AppendAllText(global, "[filter \"x\"]\n\tclean = echo ran > ../filter-ran.txt; cat\n");
        File.WriteAllText(Path.Combine(host.RepoRoot, ".gitattributes"), "* filter=x\n");

        using var response = await host.SendAsync(HttpMethod.Post, "/control/export");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("global key 'filter.x.clean'");
        File.Exists(marker).Should().BeFalse("no git command ran over the planted config");
    }

    /// <summary>
    /// GitCli's hooks directory is on the work volume too; the sandbox's git points core.hooksPath at /dev/null instead.
    /// Red: drop core.hooksPath=/dev/null from RepoConfigGuard.CommandConfig; the import's checkout then runs the
    /// planted post-checkout hook, which writes the marker.
    /// </summary>
    [Fact]
    public async Task A_hook_planted_in_the_isolation_hooks_directory_never_runs()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var remote = Remote();
        var (bundle, commit) = await HostHarness.BundleAsync(remote, MirrorData);
        using (var refused = await host.ImportAsync(bundle, "a..b", commit))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a refused import still starts GitCli, which creates its hooks directory");
        }

        var hooks = Path.Combine(host.WorkRoot, "git", ".git-isolation", "hooks");
        Directory.Exists(hooks).Should().BeTrue("the planted hook must be in the directory GitCli points core.hooksPath at");
        var hook = Path.Combine(hooks, "post-checkout");
        File.WriteAllText(hook, "#!/bin/sh\necho ran > ../hook-ran.txt\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (var accepted = await host.ImportAsync(bundle, "run/x", commit))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        (await host.WaitSettledAsync()).Imported.Should().BeTrue();
        File.Exists(Path.Combine(host.WorkRoot, "hook-ran.txt")).Should().BeFalse();
    }

    /// <summary>Red: drop the bin and obj exclude pathspecs from ExportService's git diff; the staged obj file is then in the patch.</summary>
    [Fact]
    public async Task Export_leaves_out_an_obj_file_agent_code_staged_directly()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        Directory.CreateDirectory(Path.Combine(host.RepoRoot, "Lib", "obj"));
        File.WriteAllText(Path.Combine(host.RepoRoot, "Lib", "obj", "staged.json"), "{}\n");
        File.WriteAllText(Path.Combine(host.RepoRoot, "Keep.cs"), "class Keep { }\n");
        LocalGitRemote.RunGit(host.RepoRoot, "add", "Lib/obj/staged.json");

        var patch = Encoding.UTF8.GetString(await ExportAsync(host));

        var files = patch.Split('\n').Where(l => l.StartsWith("diff --git ", StringComparison.Ordinal)).ToList();
        files.Should().ContainSingle().Which.Should().Be("diff --git a/Keep.cs b/Keep.cs");
    }

    /// <summary>
    /// A copy that cannot be made, or not in time, is an error result, not a fault. Red 1: in SandboxTools.RunAsync, run in
    /// the worktree when the copy fails; the build then answers exit 0. Red 2: drop the deadline from the copy's token
    /// and the remaining-time check; the copy then completes and the build answers exit 0.
    /// </summary>
    [Fact]
    public async Task A_copy_that_fails_or_times_out_is_an_error_result()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        var options = host.Services.GetRequiredService<SandboxToolOptions>();

        options.ScratchMaxBytes = 1;
        var oversized = await host.CallAsync("sandbox", "build");
        options.ScratchMaxBytes = long.MaxValue;
        options.BuildTimeout = TimeSpan.Zero; // the deadline is spent before the copy starts: deterministic
        var timedOut = await host.CallAsync("sandbox", "build");

        oversized.Should().StartWith("error: could not copy the workspace to run in: the workspace holds more than 1 bytes");
        timedOut.Should().StartWith("error: copying the workspace to run in timed out");
        host.Runner.Specs.Should().NotContain(s => s.Arguments[0] == "build", "no build ran without its copy");
        Directory.EnumerateFileSystemEntries(Path.Combine(host.WorkRoot, "scratch")).Should().BeEmpty();
    }

    /// <summary>
    /// A symlink in the bundle arrives as a regular file holding the target's path. Meaningful on Linux, where real links
    /// exist; on Windows git cannot make one without a privilege and writes a file either way.
    /// Red (Linux): drop core.symlinks=false from the clone's --config and from both commands' -c.
    /// </summary>
    [Fact]
    public async Task A_symlink_in_the_bundle_arrives_as_a_regular_file()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var remote = Remote();
        AddSymlink(remote, "link", "../../outside");

        await host.ImportAndSettleAsync(remote, MirrorData);

        var link = new FileInfo(Path.Combine(host.RepoRoot, "link"));
        link.LinkTarget.Should().BeNull();
        File.ReadAllText(link.FullName).Should().Be("../../outside");
    }

    /// <summary>Red: in ImportService.StartAsync, claim with a plain read and write instead of CompareExchange, with a yield between; both then answer 202.</summary>
    [Fact]
    public async Task Two_simultaneous_imports_accept_exactly_one()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var (bundle, commit) = await HostHarness.BundleAsync(Remote(), MirrorData);

        var replies = await Task.WhenAll(host.ImportAsync(bundle, "run/a", commit), host.ImportAsync(bundle, "run/b", commit));

        replies.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
        foreach (var reply in replies)
        {
            reply.Dispose();
        }
    }

    /// <summary>Red: in ImportService.StartAsync, let an IOException from the body propagate; the call then throws and the claim stays taken.</summary>
    [Fact]
    public async Task A_body_that_fails_mid_stream_releases_the_import()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var (bundle, commit) = await HostHarness.BundleAsync(Remote(), MirrorData);
        var imports = host.Services.GetRequiredService<ImportService>();

        var reply = await imports.StartAsync("run/x", commit, "App.slnx", new FailingStream(), CancellationToken.None);

        reply.Should().Be(ImportReply.BodyFailed);
        Directory.EnumerateFiles(Path.Combine(host.WorkRoot, "import")).Should().BeEmpty();
        using var accepted = await host.ImportAsync(bundle, "run/x", commit);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    /// <summary>Red: in SandboxSettings.Read, accept an empty protected-path list.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(";")]
    public async Task The_host_refuses_to_start_without_protected_paths(string paths)
    {
        var start = async () => await HostHarness.StartAsync(WorkRoot(), new Dictionary<string, string>(StringComparer.Ordinal) { [SandboxEnvironment.ProtectedPaths] = paths });

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{SandboxEnvironment.ProtectedPaths}*");
    }

    /// <summary>
    /// A build-file change the registry already reloaded for, ahead of the restore, still reloads Roslyn once that restore
    /// ran. Simulated by marking restore dirty without telling the registry. Red: drop the registry notification at the
    /// end of RestoreService.RunAsync; reload_count is then never called.
    /// </summary>
    [Fact]
    public async Task A_restore_marks_roslyn_for_a_reload()
    {
        var log = Path.Combine(_temp, "calls.log");
        await using var host = await HostHarness.StartAsync(WorkRoot(), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SandboxSettings.RoslynArgsKey] = $"{HostHarness.ServerDll};--ready-tool;list_solutions;--call-log;{log}",
            [SandboxSettings.RoslynReloadKey] = "tool:reload_count",
        });
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        (await host.CallAsync("roslyn", "echo", ("text", "first"))).Should().Be("echo:first");
        ReadLog(log).Should().NotContain("reload_count", "nothing changed yet");

        host.Services.GetRequiredService<RestoreService>().OnFilesChanged(host.RunId, ["App.csproj"]);
        (await host.CallAsync("roslyn", "echo", ("text", "second"))).Should().Be("echo:second");

        host.Runner.Restores.Should().HaveCount(2);
        ReadLog(log).Should().Contain("reload_count", "the restore marked the server for a reload");
    }

    /// <summary>
    /// A tool list that cannot be read leaves the route without tools instead of failing the request, and is read again
    /// later. Red: drop the catch in RoslynProxyTools.ListAsync; listing then fails.
    /// </summary>
    [Fact]
    public async Task A_roslyn_tool_list_that_times_out_leaves_the_route_empty_and_is_retried()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        await host.ImportAndSettleAsync(Remote(), MirrorData);
        var proxy = host.Services.GetRequiredService<RoslynProxyTools>();
        proxy.CallTimeout = TimeSpan.FromTicks(1);

        (await ToolNamesAsync(host, "roslyn")).Should().BeEmpty();

        proxy.CallTimeout = TimeSpan.FromMinutes(2);
        (await ToolNamesAsync(host, "roslyn")).Should().Contain("echo");
    }

    private static string[] EnvNames(string dump) => [.. File.ReadAllLines(dump).Select(l => l[..l.IndexOf('=', StringComparison.Ordinal)])];

    private static string[] ReadLog(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    /// <summary>Pushes a commit to main that adds <paramref name="name"/> as a git symlink (mode 120000) to <paramref name="target"/>.</summary>
    private void AddSymlink(LocalGitRemote remote, string name, string target)
    {
        var scratch = Path.Combine(_temp, "symlink-seed");
        LocalGitRemote.RunGit(_temp, "clone", "-q", "--branch", "main", remote.Url, scratch);
        var targetFile = Path.Combine(_temp, "symlink-target.txt");
        File.WriteAllText(targetFile, target);
        var blob = LocalGitRemote.RunGit(scratch, "hash-object", "-w", targetFile);
        LocalGitRemote.RunGit(scratch, "update-index", "--add", "--cacheinfo", $"120000,{blob},{name}");
        LocalGitRemote.RunGit(scratch, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "link");
        LocalGitRemote.RunGit(scratch, "push", "-q", "origin", "main");
    }

    /// <summary>Puts the token into this process's environment, as the container gives it to the host, until disposed.</summary>
    private sealed class ProcessToken : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(SandboxEnvironment.Token);

        public ProcessToken() => Environment.SetEnvironmentVariable(SandboxEnvironment.Token, HostHarness.Token);

        public void Dispose() => Environment.SetEnvironmentVariable(SandboxEnvironment.Token, _previous);
    }

    /// <summary>A body that yields a few bytes and then fails, as a client that goes away mid-upload does.</summary>
    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0)
            {
                throw new IOException("The client went away.");
            }

            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
