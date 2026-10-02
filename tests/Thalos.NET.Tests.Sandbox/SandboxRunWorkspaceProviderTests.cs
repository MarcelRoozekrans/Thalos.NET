using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Sandbox;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// A10: <see cref="SandboxRunWorkspaceProvider"/> over the real <see cref="GitMirrorStore"/> on a <see cref="LocalGitRemote"/>
/// and a <see cref="FakeSandboxRuntime"/> whose sandboxes are real sandbox hosts on loopback.
/// </summary>
public sealed class SandboxRunWorkspaceProviderTests : IAsyncLifetime
{
    private const string Original = "class Marker { }\n";

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-sandbox-provider-").FullName;
    private readonly LocalGitRemote _remote = LocalGitRemote.Create(("App.slnx", "<Solution />\n"), ("Marker.cs", Original));
    private readonly RecordingObserver _observer = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeSandboxRuntime> _runtimes = [];

    /// <summary>The runtime of the provider under test; a fake clock gets a runtime of its own.</summary>
    private FakeSandboxRuntime Runtime => _runtimes[^1];

    private string DataRoot => Path.Combine(_temp, "data");

    private string SandboxesDirectory => Path.Combine(DataRoot, "sandboxes");

    // ---------- create ----------

    /// <summary>
    /// Red 1: in Workspace, set Root to the bare SandboxId; the root assertion fails. Red 2: resolve the base from the
    /// mirror's "other" branch instead of DefaultBranch; BaseCommit differs from main's head. Red 3: skip deleting the
    /// imported bundle; a .bundle is left. Red 4: skip NotifyObserversAsync on ready; the observer hears nothing. Red 5:
    /// skip writing the Ready record; FindAsync is null. Red 6: hex-encode the token's bytes instead of base64url; its length and alphabet differ.
    /// </summary>
    [Fact]
    public async Task Create_mirrors_bundles_creates_imports_and_records_ready()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();

        var created = await provider.CreateAsync(Request(runId), CancellationToken.None);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");
        var found = await provider.FindAsync(runId, CancellationToken.None);
        var ready = await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None);
        var readiness = await provider.ReadinessAsync(runId, CancellationToken.None);
        var spec = Runtime.Specs.Single();
        var head = ready.IsSuccess ? await File.ReadAllTextAsync(Path.Combine(Runtime.WorkRootOf(spec.SandboxId), "repo", ".git", "HEAD")) : "";

        using var _ = new AssertionScope();
        found.Should().NotBeNull();
        found!.Root.Should().Be($"sandbox://{runId:N}");
        found.BaseCommit.Should().Be(_remote.HeadOf("main"));
        found.SolutionPath.Should().Be("App.slnx");
        found.Branch.Should().Be("run/feature");
        created.Value.Should().Be(found);
        Directory.EnumerateFiles(SandboxesDirectory, "*.bundle").Should().BeEmpty();
        _observer.Events.Should().Equal($"ready {runId}");
        ready.IsSuccess.Should().BeTrue(ready.IsFailure ? ready.Error.ToString() : "");
        readiness.IsSuccess.Should().BeTrue();
        readiness.Value.Imported.Should().BeTrue();
        readiness.Value.Roslyn.Should().Be("ready");
        head.Trim().Should().Be("ref: refs/heads/run/feature");
        spec.Token.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        spec.ProtectedPaths.Entries.Should().Equal(".git/", "AGENT.md");
        spec.Image.Should().Be("thalos/sandbox:test");
        (await provider.ListAsync(CancellationToken.None)).Should().Equal(found);
    }

    /// <summary>
    /// Red: in UndoCreateAsync, skip deleting the record; the provisional record is left, so the record file exists and
    /// ListAsync reports it. Red 2: wrap the runtime's error, as a ProviderError naming the sandbox create; it no longer
    /// equals the runtime's.
    /// </summary>
    [Fact]
    public async Task A_runtime_failure_leaves_no_record_and_returns_the_runtimes_error()
    {
        var provider = Provider();
        var failure = AgentError.ProviderError("the engine is unreachable");
        Runtime.CreateFailure = failure;
        var runId = Guid.NewGuid();

        var created = await provider.CreateAsync(Request(runId), CancellationToken.None);

        using var _ = new AssertionScope();
        created.IsFailure.Should().BeTrue();
        created.Error.Should().Be(failure, "the runtime's error is returned unchanged");
        File.Exists(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().BeFalse();
        Directory.EnumerateFiles(SandboxesDirectory, "*.bundle").Should().BeEmpty();
        (await provider.ListAsync(CancellationToken.None)).Should().BeEmpty();
        Runtime.Deleted.Should().BeEmpty("a failed create touched no container, and might have found another's");
        _observer.Events.Should().BeEmpty();
    }

    /// <summary>Red: in UndoCreateAsync, skip runtime.DeleteAsync; the sandbox is left running and never deleted.</summary>
    [Fact]
    public async Task An_import_failure_deletes_the_sandbox()
    {
        var provider = Provider();
        Runtime.StartWithWrongToken = true;
        var runId = Guid.NewGuid();

        var created = await provider.CreateAsync(Request(runId), CancellationToken.None);

        using var _ = new AssertionScope();
        created.IsFailure.Should().BeTrue();
        created.Error.Message.Should().Contain("import").And.Contain("401");
        Runtime.Deleted.Should().Equal(runId.ToString("N"));
        Runtime.Ids.Should().BeEmpty();
        File.Exists(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().BeFalse();
        Directory.EnumerateFiles(SandboxesDirectory, "*.bundle").Should().BeEmpty();
        _observer.Events.Should().BeEmpty();
    }

    /// <summary>
    /// R21. Red: drop the spec.Validate() call; the runtime is then asked to create a sandbox whose environment the
    /// semicolon corrupts.
    /// </summary>
    [Fact]
    public async Task An_invalid_spec_fails_before_the_runtime_is_asked()
    {
        var provider = Provider(o => o.AllowedWriteExtensions = new HashSet<string>(StringComparer.Ordinal) { ".cs;.sh" });

        var created = await provider.CreateAsync(Request(Guid.NewGuid()), CancellationToken.None);

        using var _ = new AssertionScope();
        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be(AgentErrorCode.Validation);
        created.Error.Message.Should().Contain("semicolon");
        Runtime.Specs.Should().BeEmpty();
    }

    /// <summary>
    /// Red: in ImportAsync, send the branch as <c>record.Branch + "-" + record.Token</c>, carrying the token into the
    /// sandbox; the host writes it into <c>.git/HEAD</c> and the scan finds it.
    /// </summary>
    [Fact]
    public async Task The_record_and_token_never_reach_the_sandbox()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var token = Runtime.Specs.Single().Token;
        var workRoot = Runtime.WorkRootOf(runId.ToString("N"));

        var leaks = Directory.EnumerateFiles(workRoot, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f, Encoding.Latin1).Contains(token, StringComparison.Ordinal))
            .ToList();

        using var _ = new AssertionScope();
        Directory.EnumerateFiles(workRoot, "*", SearchOption.AllDirectories).Should().NotBeEmpty("the scan covers the sandbox's checkout");
        leaks.Should().BeEmpty();
        File.ReadAllText(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().Contain(token, "the record, on the trusted side, holds it");
    }

    /// <summary>
    /// Red 1: read through the sandbox, over its workspace endpoint's read_file; the sandbox's edited text comes back.
    /// Red 2: read at the mirror's current default-branch tip instead of the record's base commit; the newer pushed text
    /// comes back.
    /// </summary>
    [Fact]
    public async Task ReadBaseFile_reads_the_mirror_even_after_the_sandbox_changed_the_file()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(Runtime.WorkRootOf(runId.ToString("N")), "repo", "Marker.cs"), "class Edited { }\n");
        PushChange("Marker.cs", "class Pushed { }\n");
        (await provider.CreateAsync(Request(Guid.NewGuid()), CancellationToken.None)).IsSuccess.Should().BeTrue("a second create fetches the push into the mirror");

        var read = await provider.ReadBaseFileAsync(runId, "Marker.cs", CancellationToken.None);
        var missing = await provider.ReadBaseFileAsync(runId, "Missing.cs", CancellationToken.None);
        var unknown = await provider.ReadBaseFileAsync(Guid.NewGuid(), "Marker.cs", CancellationToken.None);

        using var _ = new AssertionScope();
        read.IsSuccess.Should().BeTrue(read.IsFailure ? read.Error.ToString() : "");
        read.Value.Should().Be(Original);
        missing.IsSuccess.Should().BeTrue();
        missing.Value.Should().BeNull();
        unknown.IsFailure.Should().BeTrue();
    }

    // ---------- tools ----------

    /// <summary>
    /// Red 1: in ResolveAsync, drop the Running check; the exited sandbox still resolves. Red 2: derive the token from
    /// the run id instead of RandomNumberGenerator; the re-created sandbox's token equals the first.
    /// </summary>
    [Fact]
    public async Task The_endpoint_is_only_resolved_for_a_ready_running_sandbox()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        var unknown = await provider.ResolveAsync(runId, "workspace", CancellationToken.None);
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var handle = await Runtime.GetAsync(runId.ToString("N"), CancellationToken.None);
        var first = Runtime.Specs.Last().Token;

        var running = await provider.ResolveAsync(runId, "workspace", CancellationToken.None);
        await Runtime.ExitAsync(runId.ToString("N"), 0, oomKilled: false);
        var exited = await provider.ResolveAsync(runId, "workspace", CancellationToken.None);
        (await provider.RemoveAsync(runId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var removed = await provider.ResolveAsync(runId, "workspace", CancellationToken.None);
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var again = await provider.ResolveAsync(runId, "roslyn", CancellationToken.None);

        using var _ = new AssertionScope();
        unknown.Should().BeNull();
        running.Should().NotBeNull();
        running!.Endpoint.Should().Be(new Uri(handle!.BaseAddress, "mcp/workspace"));
        running.BearerToken.Should().Be(first);
        exited.Should().BeNull();
        removed.Should().BeNull();
        again.Should().NotBeNull();
        again!.BearerToken.Should().NotBe(first, "a re-created sandbox gets a new token");
        again.Endpoint.AbsolutePath.Should().EndWith("/mcp/roslyn");
    }

    /// <summary>
    /// Red 1: drop the OOM suffix in Stopped; the message lacks "killed for memory". Red 2: ask the sandbox before
    /// looking at its container; the exited sandbox's refused connection is polled until the timeout instead.
    /// </summary>
    [Fact]
    public async Task Readiness_reports_an_exited_sandbox_with_its_exit_code_and_oom_flag()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await Runtime.GetAsync(runId.ToString("N"), CancellationToken.None);

        await Runtime.ExitAsync(runId.ToString("N"), 137, oomKilled: true);
        var oom = await provider.WaitAllReadyAsync(runId, TimeSpan.FromSeconds(1), CancellationToken.None);
        var oomReadiness = await provider.ReadinessAsync(runId, CancellationToken.None);
        await Runtime.ExitAsync(runId.ToString("N"), 1, oomKilled: false);
        var plain = await provider.WaitAllReadyAsync(runId, TimeSpan.FromSeconds(1), CancellationToken.None);

        using var _ = new AssertionScope();
        oom.IsFailure.Should().BeTrue();
        oom.Error.Message.Should().Be("the run's sandbox stopped (exit 137, killed for memory)");
        oomReadiness.IsFailure.Should().BeTrue();
        oomReadiness.Error.Message.Should().Be("the run's sandbox stopped (exit 137, killed for memory)");
        plain.IsFailure.Should().BeTrue();
        plain.Error.Message.Should().Be("the run's sandbox stopped (exit 1)");
    }

    /// <summary>
    /// Red: in RemoveAsync, skip the run lock and remove at once; the removal then runs while the create's ready
    /// observer is still being told, so the remove succeeds early and the observer hears the removal before the ready.
    /// </summary>
    [Fact]
    public async Task A_runs_observer_notifications_never_overlap()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _observer.BeforeReady = async () =>
        {
            entered.TrySetResult();
            await gate.Task;
        };
        var provider = Provider();
        var runId = Guid.NewGuid();

        var creating = provider.CreateAsync(Request(runId), CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromMinutes(2));
        var early = await provider.RemoveAsync(runId, CancellationToken.None);
        gate.SetResult();
        var created = await creating;
        var late = await provider.RemoveAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        early.IsFailure.Should().BeTrue("the create holds the run while its observers hear of it");
        created.IsSuccess.Should().BeTrue();
        late.IsSuccess.Should().BeTrue();
        _observer.Events.Should().Equal($"ready {runId}", $"removing {runId}");
    }

    // ---------- remove ----------

    /// <summary>
    /// Red 1: skip runtime.DeleteAsync in RemoveLockedAsync; the sandbox survives. Red 2: skip deleting the stored patch;
    /// it survives. Red 2b: skip deleting the partial patch; it survives. Red 3: skip publishWorktrees.RemoveAsync; the publish worktree survives. Red 4: skip telling the
    /// observers; they hear nothing. Red 5: fail when the record is absent; the second remove fails.
    /// </summary>
    [Fact]
    public async Task Remove_deletes_everything_and_is_idempotent()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(Request(runId), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var publishRequest = Request(runId) with { Branch = "publish/feature", StartPoint = created.Value.BaseCommit };
        (await _publish.CreateAsync(publishRequest, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var patch = provider.Store.PatchPath(runId);
        await File.WriteAllTextAsync(patch, "diff --git a/x b/x\n");
        await File.WriteAllTextAsync(provider.Store.PatchTempPath(runId), "diff --git a/x");

        var removed = await provider.RemoveAsync(runId, CancellationToken.None);
        var again = await provider.RemoveAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        removed.IsSuccess.Should().BeTrue(removed.IsFailure ? removed.Error.ToString() : "");
        again.IsSuccess.Should().BeTrue();
        Runtime.Ids.Should().BeEmpty();
        File.Exists(patch).Should().BeFalse();
        File.Exists(provider.Store.PatchTempPath(runId)).Should().BeFalse("a park that died mid-export leaves a partial patch");
        (await _publish.FindAsync(runId, CancellationToken.None)).Should().BeNull();
        File.Exists(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().BeFalse();
        File.Exists(provider.Store.LockPath(runId)).Should().BeFalse();
        (await provider.FindAsync(runId, CancellationToken.None)).Should().BeNull();
        (await provider.ListAsync(CancellationToken.None)).Should().BeEmpty();
        _observer.Events.Should().Equal($"ready {runId}", $"removing {runId}");
    }

    // ---------- reconcile ----------

    /// <summary>
    /// Red 1: drop the OrphanGrace check; the first reconcile deletes the young unrecorded sandbox. Red 2: treat every
    /// sandbox as unrecorded; the recorded run's sandbox is deleted. Red 3: drop the ProvisionalGrace check; the young
    /// provisional record is deleted on the first reconcile.
    /// </summary>
    [Fact]
    public async Task Reconcile_deletes_an_unrecorded_sandbox_after_the_grace_and_keeps_a_recorded_one()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = Provider(clock: clock);
        var recorded = Guid.NewGuid();
        (await provider.CreateAsync(Request(recorded), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var orphan = Guid.NewGuid();
        Runtime.Seed(orphan, clock.GetUtcNow());
        var abandoned = Guid.NewGuid();
        Runtime.Seed(abandoned, clock.GetUtcNow());
        provider.Store.EnsureDirectory().Should().BeNull();
        (await provider.Store.WriteAsync(Record(abandoned, SandboxRecordState.Provisional, clock.GetUtcNow()), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var early = await provider.ReconcileAsync(CancellationToken.None);
        var idsAfterEarly = Runtime.Ids;
        clock.Advance(TimeSpan.FromMinutes(11));
        var late = await provider.ReconcileAsync(CancellationToken.None);

        using var _ = new AssertionScope();
        early.Should().Be(0);
        idsAfterEarly.Should().BeEquivalentTo([recorded.ToString("N"), orphan.ToString("N"), abandoned.ToString("N")]);
        late.Should().Be(2);
        Runtime.Ids.Should().Equal(recorded.ToString("N"));
        (await provider.FindAsync(recorded, CancellationToken.None)).Should().NotBeNull();
        File.Exists(Path.Combine(SandboxesDirectory, $"{abandoned:D}.json")).Should().BeFalse();
    }

    /// <summary>
    /// R22. Red: trust an empty runtime list, as <c>listed = true</c>; the ready run is then reported lost although the
    /// engine only could not be asked.
    /// </summary>
    [Fact]
    public async Task Reconcile_does_not_report_sandboxes_lost_when_the_runtime_lists_nothing()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();

        Runtime.ListNothing = true;
        var silent = await provider.ReconcileAsync(CancellationToken.None);
        var silentLog = _log.ToArray();
        Runtime.ListNothing = false;
        await Runtime.DeleteAsync(runId.ToString("N"), CancellationToken.None);
        Runtime.Seed(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var lost = await provider.ReconcileAsync(CancellationToken.None);

        using var _ = new AssertionScope();
        silent.Should().Be(0);
        silentLog.Should().ContainSingle(l => l.Contains("listed no sandboxes", StringComparison.Ordinal));
        silentLog.Should().NotContain(l => l.Contains("SandboxLost", StringComparison.Ordinal));
        lost.Should().Be(0);
        _log.Should().ContainSingle(l => l.Contains("SandboxLost", StringComparison.Ordinal) && l.Contains(runId.ToString(), StringComparison.Ordinal));
        (await provider.FindAsync(runId, CancellationToken.None)).Should().NotBeNull("a lost sandbox's record is kept");
    }

    // ---------- export ----------

    /// <summary>Red: drop both maxBytes checks in ExportToFileAsync; the oversized patch is stored and the call succeeds.</summary>
    [Fact]
    public async Task Export_stops_at_the_byte_limit_and_leaves_no_file()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(Runtime.WorkRootOf(runId.ToString("N")), "repo", "Marker.cs"), new string('x', 4096));
        var handle = (await Runtime.GetAsync(runId.ToString("N"), CancellationToken.None))!;
        var token = Runtime.Specs.Single().Token;
        using var http = new HttpClient();
        var control = new SandboxControlClient(http);
        var small = Path.Combine(_temp, "small.patch");
        var large = Path.Combine(_temp, "large.patch");

        var refused = await control.ExportToFileAsync(handle, token, _remote.HeadOf("main"), small, 100, CancellationToken.None);
        var stored = await control.ExportToFileAsync(handle, token, _remote.HeadOf("main"), large, 1024 * 1024, CancellationToken.None);

        using var _ = new AssertionScope();
        refused.IsFailure.Should().BeTrue();
        File.Exists(small).Should().BeFalse();
        stored.IsSuccess.Should().BeTrue(stored.IsFailure ? stored.Error.ToString() : "");
        File.ReadAllText(large).Should().Contain("Marker.cs");
    }

    // ---------- registration ----------

    /// <summary>
    /// Red 1: drop the IRunToolServerReadiness registration; the MCP registry answers it. Red 2: register the provider
    /// with a second, separate instance for IRunToolEndpointResolver; it is no longer the same object. Red 3: drop the
    /// ProtectedPaths check; an empty set is accepted. Red 4: drop the sandbox AddRemoteRunTools; its remote source is
    /// missing. Red 5: give the patch applier its own GitWorkspaceOptions; it no longer shares the mirror's instance.
    /// </summary>
    [Fact]
    public async Task UseSandboxRunWorkspaces_maps_every_interface_to_one_provider_and_validates_its_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISandboxRuntime>(Runtime);
        services.AddThalos(t => t
            .AddMcpServer("roslyn", new McpServerDefinition { Type = "stdio", Command = "dotnet", Args = [LoopbackSandbox.ServerDll, "--host"], RunScoped = new RunScopedMcpDefinition { Remote = true } })
            .UseSandboxRunWorkspaces(o =>
            {
                o.DataRoot = DataRoot;
                o.Image = "thalos/sandbox:test";
                o.ProtectedPaths.Add(".git/");
            }));
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<SandboxRunWorkspaceProvider>();

        using var _ = new AssertionScope();
        sp.GetRequiredService<IRunWorkspaceProvider>().Should().BeSameAs(provider);
        sp.GetRequiredService<IRunBaseFileReader>().Should().BeSameAs(provider);
        sp.GetRequiredService<IRunToolEndpointResolver>().Should().BeSameAs(provider);
        sp.GetRequiredService<IParkableRunWorkspaceProvider>().Should().BeSameAs(provider);
        sp.GetRequiredService<IRunWorkspaceHandoff>().Should().BeSameAs(provider);
        sp.GetRequiredService<IRunToolServerReadiness>().Should().BeSameAs(provider);
        sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle(s => s is SandboxReconcileService);
        var sources = sp.GetServices<IToolSource>().ToList();
        sources.OfType<RemoteRunToolSource>().Where(r => r.SchemaSource is not null).Select(r => r.Name)
            .Should().BeEquivalentTo([RunWorkspaceToolOptions.SourceName, SandboxToolOptions.SourceName]);
        sources.Where(t => t is not RemoteRunToolSource && t.Name is RunWorkspaceToolOptions.SourceName or SandboxToolOptions.SourceName)
            .Should().BeEmpty("on the API the workspace and sandbox tools are remote, never local");
        var shared = sp.GetRequiredService<GitMirrorStore>().Options;
        shared.DataRoot.Should().Be(Path.Combine(DataRoot, "publish"));
        sp.GetRequiredService<GitPatchApplier>().Options.Should().BeSameAs(shared);
        sp.GetRequiredService<SandboxPublishWorktrees>().Inner.Options.Should().BeSameAs(shared);
        Invalid(o => o.DataRoot = "relative").Should().Throw<ArgumentException>().WithMessage("*DataRoot*");
        Invalid(o => o.Image = " ").Should().Throw<ArgumentException>().WithMessage("*Image*");
        Invalid(o => o.ProtectedPaths.Clear()).Should().Throw<ArgumentException>().WithMessage("*ProtectedPaths*");
    }

    /// <summary>
    /// I1: a run-scoped MCP server that is not remote would be started on the host, in a sandbox:// root. Red: drop
    /// ThrowIfLocalRunScopedServers; the provider is built and the host would spawn the server.
    /// </summary>
    [Fact]
    public async Task A_local_run_scoped_MCP_server_is_refused_when_the_provider_is_built()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISandboxRuntime>(Runtime);
        services.AddThalos(t => t
            .UseSandboxRunWorkspaces(o =>
            {
                o.DataRoot = DataRoot;
                o.Image = "thalos/sandbox:test";
                o.ProtectedPaths.Add(".git/");
            })
            .AddMcpServer("roslyn", new McpServerDefinition { Type = "stdio", Command = "dotnet", Args = [LoopbackSandbox.ServerDll], RunScoped = new RunScopedMcpDefinition() }));
        await using var sp = services.BuildServiceProvider();

        var build = () => sp.GetRequiredService<IRunWorkspaceProvider>();

        build.Should().Throw<InvalidOperationException>().WithMessage("*remote*roslyn*");
    }

    // ---------- record integrity ----------

    /// <summary>
    /// I2: a record that is not well formed for its run is treated as corrupt: not found, not resolved, not listed, and
    /// not removed, so nothing acts on it. Red 1: drop the SandboxId check. Red 2: drop the blank-token check. Red 3:
    /// drop the full-sha check on BaseCommit. Reds 4 to 7: drop Repository, Remote, DefaultBranch or Branch from the
    /// blank-field condition. Each red makes its case's record found and removable again.
    /// </summary>
    [Theory]
    [InlineData("sandbox-id")]
    [InlineData("token")]
    [InlineData("base-commit")]
    [InlineData("repository")]
    [InlineData("remote")]
    [InlineData("default-branch")]
    [InlineData("branch")]
    public async Task A_tampered_record_fails_closed(string tamper)
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        Runtime.Seed(runId, DateTimeOffset.UtcNow);
        var record = Record(runId, SandboxRecordState.Ready, DateTimeOffset.UtcNow);
        record = tamper switch
        {
            "sandbox-id" => record with { SandboxId = Guid.NewGuid().ToString("N") },
            "token" => record with { Token = "" },
            "repository" => record with { Repository = " " },
            "remote" => record with { Remote = " " },
            "default-branch" => record with { DefaultBranch = " " },
            "branch" => record with { Branch = " " },
            _ => record with { BaseCommit = record.BaseCommit[..12] },
        };
        provider.Store.EnsureDirectory().Should().BeNull();
        (await provider.Store.WriteAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var found = await provider.FindAsync(runId, CancellationToken.None);
        var listed = await provider.ListAsync(CancellationToken.None);
        var removed = await provider.RemoveAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        found.Should().BeNull();
        listed.Should().BeEmpty();
        removed.IsFailure.Should().BeTrue("an unreadable record is left for an operator, never acted on");
        Runtime.Deleted.Should().BeEmpty();
    }

    /// <summary>
    /// I2: a handle the runtime answers for this run's sandbox id but naming another run is never talked to. Red: drop
    /// the RunId comparisons in ResolveAsync, WaitAllReadyAsync and ReadinessAsync; the endpoint resolves and the waits
    /// succeed.
    /// </summary>
    [Fact]
    public async Task A_handle_of_another_run_is_never_used()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        (await provider.CreateAsync(Request(runId), CancellationToken.None)).IsSuccess.Should().BeTrue();
        Runtime.Reassign(runId.ToString("N"), Guid.NewGuid());

        var resolved = await provider.ResolveAsync(runId, "workspace", CancellationToken.None);
        var waited = await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None);
        var readiness = await provider.ReadinessAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        resolved.Should().BeNull();
        waited.IsFailure.Should().BeTrue();
        waited.Error.Message.Should().Contain("another run");
        readiness.IsFailure.Should().BeTrue();
        readiness.Error.Message.Should().Contain("another run");
    }

    // ---------- create races and cancellation ----------

    /// <summary>
    /// A create cancelled after its container started leaves nothing. Red: set SandboxAttempted only after the runtime
    /// answered; the cancelled create's container is never deleted.
    /// </summary>
    [Fact]
    public async Task A_create_cancelled_mid_way_cleans_up()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        Runtime.AfterHostStarted = cts.Cancel;

        var create = async () => await provider.CreateAsync(Request(runId), cts.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();
        using var _ = new AssertionScope();
        Runtime.Ids.Should().BeEmpty();
        Runtime.Deleted.Should().Equal(runId.ToString("N"));
        File.Exists(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().BeFalse();
        Directory.EnumerateFiles(SandboxesDirectory, "*.bundle").Should().BeEmpty();
        _observer.Events.Should().BeEmpty();
    }

    /// <summary>
    /// Two creates for one run: exactly one wins and only it reaches the runtime. Red: in CreateAsync, treat a held run
    /// lock as free, skip the existing-record check, and write the claim with WriteAsync instead of TryClaimAsync; both
    /// creates then reach the runtime.
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_for_one_run_have_one_winner()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();

        var results = await Task.WhenAll(
            provider.CreateAsync(Request(runId), CancellationToken.None).AsTask(),
            provider.CreateAsync(Request(runId), CancellationToken.None).AsTask());

        using var _ = new AssertionScope();
        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => r.IsFailure).Error.Code.Should().Be(AgentErrorCode.Validation);
        Runtime.Specs.Should().ContainSingle();
        (await provider.FindAsync(runId, CancellationToken.None)).Should().NotBeNull();
    }

    /// <summary>
    /// A removal that failed left its record removing; the next reconcile finishes it. Red: drop the Removing case in
    /// ReconcileRecordAsync; the record and its sandbox stay.
    /// </summary>
    [Fact]
    public async Task Reconcile_finishes_a_removing_record()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();
        Runtime.Seed(runId, DateTimeOffset.UtcNow);
        provider.Store.EnsureDirectory().Should().BeNull();
        (await provider.Store.WriteAsync(Record(runId, SandboxRecordState.Removing, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var deleted = await provider.ReconcileAsync(CancellationToken.None);

        using var _ = new AssertionScope();
        deleted.Should().Be(1);
        Runtime.Ids.Should().BeEmpty();
        File.Exists(Path.Combine(SandboxesDirectory, $"{runId:D}.json")).Should().BeFalse();
        _observer.Events.Should().Equal($"removing {runId}");
    }


    /// <summary>
    /// A record whose processing throws is logged and the pass goes on to the other records and the orphans. Red: remove
    /// the per-record try in ReconcileAsync; the runtime's exception escapes the reconcile.
    /// </summary>
    [Fact]
    public async Task Reconcile_logs_a_record_that_throws_and_settles_the_rest()
    {
        var provider = Provider();
        var failing = Guid.NewGuid();
        var removing = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        Runtime.Seed(failing, DateTimeOffset.UtcNow);
        Runtime.Seed(removing, DateTimeOffset.UtcNow);
        Runtime.Seed(orphan, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(11));
        Runtime.ThrowOnDelete[failing.ToString("N")] = true;
        provider.Store.EnsureDirectory().Should().BeNull();
        (await provider.Store.WriteAsync(Record(failing, SandboxRecordState.Removing, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await provider.Store.WriteAsync(Record(removing, SandboxRecordState.Removing, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var deleted = await provider.ReconcileAsync(CancellationToken.None);

        using var _ = new AssertionScope();
        deleted.Should().Be(2, "the other removing record and the orphan are settled");
        Runtime.Ids.Should().Equal(failing.ToString("N"));
        File.Exists(Path.Combine(SandboxesDirectory, $"{failing:D}.json")).Should().BeTrue("the failing record is left for the next pass");
        File.Exists(Path.Combine(SandboxesDirectory, $"{removing:D}.json")).Should().BeFalse();
        _log.Should().ContainSingle(l => l.Contains($"Reconciling {failing}", StringComparison.Ordinal) && l.Contains("the runtime failed", StringComparison.Ordinal));
    }

    // ---------- plumbing ----------

    private SandboxPublishWorktrees _publish = null!;

    public Task InitializeAsync()
    {
        _runtimes.Add(new FakeSandboxRuntime(Path.Combine(_temp, "sandboxes"), TimeProvider.System));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var runtime in _runtimes)
        {
            await runtime.DisposeAsync();
        }

        _remote.Dispose();
        try
        {
            DeleteReadOnly(_temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A git or Roslyn child still holding a file; the temp directory is cleaned by the OS.
        }
    }

    private Func<IServiceProvider> Invalid(Action<SandboxOptions> spoil) => () =>
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.UseSandboxRunWorkspaces(o =>
        {
            o.DataRoot = DataRoot;
            o.Image = "thalos/sandbox:test";
            o.ProtectedPaths.Add(".git/");
            spoil(o);
        }));
        return services.BuildServiceProvider();
    };

    private SandboxRunWorkspaceProvider Provider(Action<SandboxOptions>? configure = null, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        if (clock is FakeTimeProvider)
        {
            _runtimes.Add(new FakeSandboxRuntime(Path.Combine(_temp, "sandboxes"), clock));
        }

        var options = new SandboxOptions { DataRoot = DataRoot, Image = "thalos/sandbox:test" };
        options.ProtectedPaths.Add(".git/");
        options.ProtectedPaths.Add("AGENT.md");
        configure?.Invoke(options);
        var publish = new GitWorkspaceOptions { DataRoot = Path.Combine(DataRoot, "publish") };
        _publish = new SandboxPublishWorktrees(new GitWorktreeWorkspaceProvider(publish, [], NullLogger<GitWorktreeWorkspaceProvider>.Instance, clock));
        var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLoggers(_log)));
        return new SandboxRunWorkspaceProvider(
            options,
            Runtime,
            new GitMirrorStore(publish, NullLogger<GitMirrorStore>.Instance),
            _publish,
            new GitPatchApplier(publish, NullLogger<GitPatchApplier>.Instance),
            [_observer],
            new SandboxControlClient(new HttpClient()),
            loggers.CreateLogger<SandboxRunWorkspaceProvider>(),
            clock)
        {
            ReadyPollInterval = TimeSpan.FromMilliseconds(200),
        };
    }

    private RunWorkspaceRequest Request(Guid runId) => new(runId, "repo", _remote.Url, "main", "run/feature", "App.slnx");

    private SandboxRecord Record(Guid runId, SandboxRecordState state, DateTimeOffset createdAt) =>
        new(runId, "repo", _remote.Url, "main", "run/feature", "App.slnx", _remote.HeadOf("main"), runId.ToString("N"), new string('t', 43), state, createdAt);

    /// <summary>Commits <paramref name="content"/> as <paramref name="name"/> on the remote's main.</summary>
    private void PushChange(string name, string content)
    {
        var clone = Path.Combine(_temp, "push-" + Guid.NewGuid().ToString("N"));
        Git(_temp, "clone", "--branch", "main", _remote.Url, clone);
        File.WriteAllText(Path.Combine(clone, name), content);
        Git(clone, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-am", "change");
        Git(clone, "push", "origin", "main");
    }

    private static void Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
    }

    private static void DeleteReadOnly(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Records each notification as <c>ready &lt;run&gt;</c> or <c>removing &lt;run&gt;</c>, in order.</summary>
    private sealed class RecordingObserver : IRunWorkspaceObserver
    {
        private readonly ConcurrentQueue<string> _events = new();

        public Func<Task>? BeforeReady { get; set; }

        public IReadOnlyList<string> Events => [.. _events];

        public async ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
        {
            if (BeforeReady is { } before)
            {
                await before();
            }

            _events.Enqueue($"ready {workspace.RunId}");
        }

        public ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
        {
            _events.Enqueue($"removing {workspace.RunId}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLoggers(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
