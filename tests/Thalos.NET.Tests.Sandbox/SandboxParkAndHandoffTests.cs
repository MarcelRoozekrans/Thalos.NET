using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// A11: <see cref="SandboxRunWorkspaceProvider.ParkAsync"/> and <see cref="SandboxRunWorkspaceProvider.CheckoutForPublishAsync"/>
/// over A10's harness: the real <see cref="GitMirrorStore"/> and publish worktrees on a <see cref="LocalGitRemote"/>, and a
/// <see cref="FakeSandboxRuntime"/> whose sandboxes are real sandbox hosts on loopback. A change "written around the
/// tools" is written straight into the in-process host's work root, as agent-run code inside the container could.
/// </summary>
public sealed class SandboxParkAndHandoffTests : IAsyncLifetime
{
    private const string Original = "class Marker { }\n";
    private const string Edited = "class Edited { }\n";

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-sandbox-park-").FullName;
    private readonly LocalGitRemote _remote = LocalGitRemote.Create(("App.slnx", "<Solution />\n"), ("Marker.cs", Original));
    private readonly RecordingObserver _observer = new();
    private readonly ConcurrentQueue<string> _log = new();
    private FakeSandboxRuntime? _runtime;
    private SandboxPublishWorktrees? _publish;

    private FakeSandboxRuntime Runtime => _runtime!;

    private SandboxPublishWorktrees Publish => _publish!;

    private string DataRoot => Path.Combine(_temp, "data");

    // ---------- park ----------

    /// <summary>
    /// Red: in ParkLockedAsync, delete the sandbox before the export; the export finds no host and the park fails.
    /// Red 2: skip NotifyObserversAsync; the observer never hears of the removal.
    /// </summary>
    [Fact]
    public async Task Park_exports_then_deletes_and_records_parked()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);
        var again = await provider.ParkAsync(runId, CancellationToken.None);
        var patch = provider.Store.PatchPath(runId);

        using var _ = new AssertionScope();
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        record.State.Should().Be(SandboxRecordState.Parked);
        record.PatchPath.Should().Be(patch);
        record.PatchMissing.Should().BeFalse();
        File.ReadAllText(patch).Should().Contain("Marker.cs").And.Contain("+class Edited { }");
        File.Exists(provider.Store.PatchTempPath(runId)).Should().BeFalse();
        Runtime.Ids.Should().BeEmpty();
        Runtime.Deleted.Should().Equal(runId.ToString("N"));
        _observer.Events.Should().Equal($"ready {runId}", $"removing {runId}");
        again.IsSuccess.Should().BeTrue("a parked run parks again at once");
        (await provider.FindAsync(runId, CancellationToken.None)).Should().NotBeNull("a parked run still has a workspace to publish");
        (await provider.ResolveAsync(runId, "workspace", CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Red: in ParkLockedAsync, delete the sandbox before returning a failed export; the sandbox is gone. Red 2: write
    /// the record back as ready on a failed export; its state is not exporting.
    /// </summary>
    [Fact]
    public async Task A_failed_export_keeps_the_sandbox_and_the_next_park_retries()
    {
        var (provider, runId) = await ReadyRunAsync();
        var id = runId.ToString("N");
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        var host = Runtime.HostOf(id);
        host.RefuseExport = true;

        var failed = await provider.ParkAsync(runId, CancellationToken.None);
        var exporting = await RecordOf(provider, runId);
        var keptIds = Runtime.Ids.ToList();
        var deletedAfterFailure = Runtime.Deleted.ToList();
        var eventsAfterFailure = _observer.Events;
        var patchAfterFailure = File.Exists(provider.Store.PatchPath(runId));
        host.RefuseExport = false;
        var retried = await provider.ParkAsync(runId, CancellationToken.None);
        var parked = await RecordOf(provider, runId);

        using var _ = new AssertionScope();
        failed.IsFailure.Should().BeTrue();
        failed.Error.Message.Should().Contain("export").And.Contain("503");
        exporting.State.Should().Be(SandboxRecordState.Exporting);
        keptIds.Should().Equal(id);
        deletedAfterFailure.Should().BeEmpty();
        eventsAfterFailure.Should().Equal($"ready {runId}");
        patchAfterFailure.Should().BeFalse();
        File.Exists(provider.Store.PatchTempPath(runId)).Should().BeFalse();
        retried.IsSuccess.Should().BeTrue(retried.IsFailure ? retried.Error.ToString() : "");
        parked.State.Should().Be(SandboxRecordState.Parked);
        File.ReadAllText(provider.Store.PatchPath(runId)).Should().Contain("+class Edited { }");
    }

    /// <summary>Red: pass long.MaxValue as the export's byte cap instead of PatchLimits.MaxPatchBytes; the park succeeds.</summary>
    [Fact]
    public async Task An_oversized_export_is_refused()
    {
        var (provider, runId) = await ReadyRunAsync(o => o.PatchLimits = new PatchApplyLimits(MaxPatchBytes: 512));
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Big.cs"), string.Concat(Enumerable.Repeat("// a long line of agent output\n", 200)));

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);

        using var _ = new AssertionScope();
        parked.IsFailure.Should().BeTrue();
        parked.Error.Message.Should().Contain("larger than 512 bytes");
        record.State.Should().Be(SandboxRecordState.Exporting);
        File.Exists(provider.Store.PatchPath(runId)).Should().BeFalse();
        File.Exists(provider.Store.PatchTempPath(runId)).Should().BeFalse();
        Runtime.Ids.Should().Equal(runId.ToString("N"));
    }

    /// <summary>
    /// R22. Red: treat a null answer from GetAsync as a missing container; the park succeeds with PatchMissing and the
    /// sandbox is deleted.
    /// </summary>
    [Fact]
    public async Task An_unreachable_runtime_fails_the_park_and_never_records_a_lost_sandbox()
    {
        var (provider, runId) = await ReadyRunAsync();
        Runtime.Unreachable = true;

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);

        using var _ = new AssertionScope();
        parked.IsFailure.Should().BeTrue();
        parked.Error.Message.Should().Contain("could not be found, or the runtime could not be asked");
        checkout.IsFailure.Should().BeTrue();
        record.State.Should().Be(SandboxRecordState.Ready);
        record.PatchMissing.Should().BeFalse();
        Runtime.Deleted.Should().BeEmpty();
        _observer.Events.Should().Equal($"ready {runId}");
        (await Publish.FindAsync(runId, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Red: in StorePatchAsync, take a missing sandbox as unreachable and fail; the park fails. Red 2: in
    /// ReadPublishableAsync, skip the PatchMissing check; the checkout fails on a patch path instead, with another message.
    /// The sandbox is lost as the Docker runtime reports a container the engine answers 404 for: a Missing handle.
    /// </summary>
    [Fact]
    public async Task A_lost_sandbox_parks_with_no_patch_and_publish_says_so()
    {
        var (provider, runId) = await ReadyRunAsync();
        await Runtime.LoseAsync(runId.ToString("N"));

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        record.State.Should().Be(SandboxRecordState.Parked);
        record.PatchMissing.Should().BeTrue();
        record.PatchPath.Should().BeNull();
        _observer.Events.Should().Equal($"ready {runId}", $"removing {runId}");
        _log.Should().Contain(l => l.Contains("SandboxLost", StringComparison.Ordinal) && l.Contains(runId.ToString(), StringComparison.Ordinal));
        checkout.IsFailure.Should().BeTrue();
        checkout.Error.Message.Should().Be("the run's sandbox was lost before its changes were exported: it no longer existed");
        (await Publish.FindAsync(runId, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Red: in StorePatchAsync, ignore a stored patch when the sandbox is missing; the run parks with PatchMissing and
    /// its exported changes are lost.
    /// </summary>
    [Fact]
    public async Task A_park_stopped_after_its_export_keeps_the_stored_patch_once_the_sandbox_is_gone()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        Runtime.DeleteFailure = AgentError.ProviderError("the engine is busy");
        var interrupted = await provider.ParkAsync(runId, CancellationToken.None);
        Runtime.DeleteFailure = null;
        await Runtime.LoseAsync(runId.ToString("N"));

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        interrupted.IsFailure.Should().BeTrue();
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        record.PatchMissing.Should().BeFalse();
        record.PatchPath.Should().Be(provider.Store.PatchPath(runId));
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        File.ReadAllText(Path.Combine(checkout.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    // ---------- checkout ----------

    /// <summary>
    /// Red: in ApplyToNewWorktreeAsync, omit StartPoint; the worktree is cut from main's new tip, so its base differs
    /// and the pushed file is there.
    /// </summary>
    [Fact]
    public async Task Checkout_applies_the_patch_at_the_base_commit_even_after_main_moved()
    {
        var (provider, runId) = await ReadyRunAsync();
        var baseCommit = _remote.HeadOf("main");
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        PushChange("Other.cs", "class Other { }\n");

        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        _remote.HeadOf("main").Should().NotBe(baseCommit, "main moved after the run started");
        checkout.Value.Root.Should().Be(Path.Combine(DataRoot, "publish", "runs", runId.ToString()));
        checkout.Value.BaseCommit.Should().Be(baseCommit);
        Git(checkout.Value.Root, "rev-parse", "HEAD").Trim().Should().Be(baseCommit);
        File.ReadAllText(Path.Combine(checkout.Value.Root, "Marker.cs")).Should().Be(Edited);
        File.Exists(Path.Combine(checkout.Value.Root, "Other.cs")).Should().BeFalse();
        Git(checkout.Value.Root, "diff", "--cached", "--name-only").Trim().Should().Be("Marker.cs");
    }

    /// <summary>
    /// S5. Red: in ApplyToNewWorktreeAsync, pass an empty ProtectedPathSet instead of the options'; the checkout
    /// succeeds and the workflow file is staged for publishing. Red 2: skip the RemoveAsync after a refused apply; the
    /// worktree is left.
    /// </summary>
    [Fact]
    public async Task Checkout_refuses_a_dot_github_change_written_around_the_tools_and_leaves_no_worktree()
    {
        var (provider, runId) = await ReadyRunAsync(o => o.ProtectedPaths.Add(".github/"));
        var repo = RepoOf(runId);
        Directory.CreateDirectory(Path.Combine(repo, ".github", "workflows"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".github", "workflows", "evil.yml"), "on: push\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Marker.cs"), Edited);

        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        checkout.IsFailure.Should().BeTrue("a change to a protected path fails publish");
        checkout.Error.ToString().Should().Contain(".github");
        File.ReadAllText(provider.Store.PatchPath(runId)).Should().Contain(".github/workflows/evil.yml", "the sandbox exported it; the publish side is the control");
        (await Publish.FindAsync(runId, CancellationToken.None)).Should().BeNull();
        Directory.Exists(Path.Combine(DataRoot, "publish", "runs", runId.ToString())).Should().BeFalse();
    }

    /// <summary>
    /// Red: drop the FindAsync reuse in CheckoutLockedAsync; the second checkout finds the worktree's claim and fails.
    /// Red 2: skip writing PatchApplied; the second checkout rebuilds the worktree and the file is gone.
    /// </summary>
    [Fact]
    public async Task Checkout_twice_returns_the_same_worktree_and_keeps_a_file_written_between()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);

        var first = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.ToString() : "");
        await File.WriteAllTextAsync(Path.Combine(first.Value.Root, "AGENT.md"), "standing instructions\n");
        var second = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        second.IsSuccess.Should().BeTrue(second.IsFailure ? second.Error.ToString() : "");
        second.Value.Root.Should().Be(first.Value.Root);
        File.ReadAllText(Path.Combine(second.Value.Root, "AGENT.md")).Should().Be("standing instructions\n");
        File.ReadAllText(Path.Combine(second.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    /// <summary>
    /// Red: in CheckoutLockedAsync, return a found worktree whatever PatchApplied says; the bare-base worktree is
    /// returned and Marker.cs is the original.
    /// </summary>
    [Fact]
    public async Task A_publish_worktree_left_without_its_patch_is_rebuilt()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        (await provider.ParkAsync(runId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var request = new RunWorkspaceRequest(runId, "repo", _remote.Url, "main", "run/feature", "App.slnx") { StartPoint = _remote.HeadOf("main") };
        (await Publish.CreateAsync(request, CancellationToken.None)).IsSuccess.Should().BeTrue("a checkout died after creating the worktree");

        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        File.ReadAllText(Path.Combine(checkout.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    /// <summary>R16. Red: in StorePatchAsync, fail an export whose patch is empty; the park fails and nothing is checked out.</summary>
    [Fact]
    public async Task An_empty_export_parks_and_checks_out_a_clean_worktree()
    {
        var (provider, runId) = await ReadyRunAsync();

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        new FileInfo(provider.Store.PatchPath(runId)).Length.Should().Be(0);
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        Git(checkout.Value.Root, "status", "--porcelain").Should().BeEmpty();
    }

    /// <summary>
    /// Red: in ParkAsync, succeed for a record that is not ready or exporting; parking a run with no sandbox succeeds.
    /// Red 2: drop the record check ahead of CheckoutForPublishAsync's lock; a lock file is left for the unknown run.
    /// </summary>
    [Fact]
    public async Task Parking_or_checking_out_a_run_with_no_sandbox_fails_and_leaves_nothing()
    {
        var provider = Provider();
        var runId = Guid.NewGuid();

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        parked.IsFailure.Should().BeTrue();
        parked.Error.Code.Should().Be(AgentErrorCode.Validation);
        checkout.IsFailure.Should().BeTrue();
        checkout.Error.Code.Should().Be(AgentErrorCode.Validation);
        File.Exists(provider.Store.LockPath(runId)).Should().BeFalse();
    }

    // ---------- exited sandboxes (R36) ----------

    /// <summary>
    /// Red: in RestartAsync, skip runtime.StartAsync; the sandbox never answers, the park fails after RestartTimeout and
    /// nothing is started. Red 2: in ExportToFileAsync, drop the commit query; the restarted host has no import
    /// in memory and refuses the export with 409.
    /// </summary>
    [Fact]
    public async Task An_exited_sandbox_is_started_again_exported_and_deleted()
    {
        var (provider, runId) = await ReadyRunAsync(o => o.RestartTimeout = TimeSpan.FromSeconds(5));
        var id = runId.ToString("N");
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        await Runtime.ExitAsync(id, 137, oomKilled: true);

        var parked = await provider.ParkAsync(runId, CancellationToken.None);
        var record = await RecordOf(provider, runId);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        Runtime.Started.Should().Equal(id);
        Runtime.Ids.Should().BeEmpty("the restarted sandbox is deleted once exported");
        record.State.Should().Be(SandboxRecordState.Parked);
        record.PatchMissing.Should().BeFalse();
        record.ExportAttempts.Should().Be(1);
        File.ReadAllText(provider.Store.PatchPath(runId)).Should().Contain("+class Edited { }");
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        File.ReadAllText(Path.Combine(checkout.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    /// <summary>
    /// Red: in StorePatchAsync, drop the MaxRestartAttempts check; the third park tries again, fails, and the record
    /// stays exporting. Red 2: in ReadPublishableAsync, leave the reason out of the message; it no longer names it.
    /// </summary>
    [Fact]
    public async Task An_exited_sandbox_that_cannot_be_restarted_parks_without_a_patch_after_two_attempts()
    {
        var (provider, runId) = await ReadyRunAsync();
        var id = runId.ToString("N");
        await Runtime.ExitAsync(id, 1, oomKilled: false);
        Runtime.StartFailure = AgentError.ProviderError("the engine refused to start it");

        var first = await provider.ParkAsync(runId, CancellationToken.None);
        var afterFirst = await RecordOf(provider, runId);
        var second = await provider.ParkAsync(runId, CancellationToken.None);
        var afterSecond = await RecordOf(provider, runId);
        var third = await provider.ParkAsync(runId, CancellationToken.None);
        var parked = await RecordOf(provider, runId);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        first.IsFailure.Should().BeTrue();
        afterFirst.State.Should().Be(SandboxRecordState.Exporting);
        afterFirst.ExportAttempts.Should().Be(1);
        second.IsFailure.Should().BeTrue();
        afterSecond.ExportAttempts.Should().Be(2);
        third.IsSuccess.Should().BeTrue(third.IsFailure ? third.Error.ToString() : "");
        parked.State.Should().Be(SandboxRecordState.Parked);
        parked.PatchMissing.Should().BeTrue();
        parked.PatchMissingReason.Should().Be("could not be restarted to export");
        Runtime.Ids.Should().BeEmpty("the exited sandbox is deleted once given up on");
        _log.Should().Contain(l => l.Contains("SandboxLost", StringComparison.Ordinal) && l.Contains("2 restarts", StringComparison.Ordinal));
        checkout.IsFailure.Should().BeTrue();
        checkout.Error.Message.Should().Be("the run's sandbox was lost before its changes were exported: could not be restarted to export");
    }

    /// <summary>
    /// N1. Red: in StorePatchAsync, drop the stored-patch check on the attempt cap; the third park parks the run without a
    /// patch although the second exported it.
    /// </summary>
    [Fact]
    public async Task A_patch_the_last_restart_attempt_stored_is_kept_once_the_attempts_are_spent()
    {
        var (provider, runId) = await ReadyRunAsync(o => o.RestartTimeout = TimeSpan.FromSeconds(10));
        var id = runId.ToString("N");
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        await Runtime.ExitAsync(id, 1, oomKilled: false);
        Runtime.StartFailure = AgentError.ProviderError("the engine refused to start it");
        var first = await provider.ParkAsync(runId, CancellationToken.None);
        Runtime.StartFailure = null;
        Runtime.DeleteFailure = AgentError.ProviderError("the engine is busy");
        var second = await provider.ParkAsync(runId, CancellationToken.None);
        var afterSecond = await RecordOf(provider, runId);
        Runtime.DeleteFailure = null;

        var third = await provider.ParkAsync(runId, CancellationToken.None);
        var parked = await RecordOf(provider, runId);
        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        first.IsFailure.Should().BeTrue();
        second.IsFailure.Should().BeTrue("the export succeeded but the delete failed");
        afterSecond.ExportAttempts.Should().Be(2);
        File.Exists(provider.Store.PatchPath(runId)).Should().BeTrue();
        third.IsSuccess.Should().BeTrue(third.IsFailure ? third.Error.ToString() : "");
        parked.PatchMissing.Should().BeFalse();
        parked.PatchPath.Should().Be(provider.Store.PatchPath(runId));
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        File.ReadAllText(Path.Combine(checkout.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    /// <summary>
    /// R37a. Red: in RestartAndExportAsync, drop the refund on the caller's cancellation; the cancelled attempt stays
    /// counted.
    /// </summary>
    [Fact]
    public async Task A_restart_attempt_the_caller_cancels_is_given_back()
    {
        var (provider, runId) = await ReadyRunAsync();
        await Runtime.ExitAsync(runId.ToString("N"), 1, oomKilled: false);
        using var cts = new CancellationTokenSource();
        Runtime.AfterStart = cts.Cancel;

        var park = async () => await provider.ParkAsync(runId, cts.Token);
        await park.Should().ThrowAsync<OperationCanceledException>();
        var record = await RecordOf(provider, runId);

        using var _ = new AssertionScope();
        Runtime.Started.Should().ContainSingle("the attempt was made");
        record.State.Should().Be(SandboxRecordState.Exporting);
        record.ExportAttempts.Should().Be(0, "a park its caller cancelled is not the sandbox's failure");
    }

    /// <summary>
    /// R37b. Red: in StorePatchAsync, skip the deadline check; the short-budget park starts the sandbox and succeeds.
    /// </summary>
    [Fact]
    public async Task A_budget_too_small_for_a_restart_begins_none_and_counts_none()
    {
        var (provider, runId) = await ReadyRunAsync(o => o.RestartTimeout = TimeSpan.FromSeconds(30));
        await Runtime.ExitAsync(runId.ToString("N"), 1, oomKilled: false);

        var short_ = await provider.ParkAsync(runId, TimeSpan.FromSeconds(5), CancellationToken.None);
        var afterShort = await RecordOf(provider, runId);
        var started = Runtime.Started.Count;
        var ample = await provider.ParkAsync(runId, TimeSpan.FromHours(1), CancellationToken.None);

        using var _ = new AssertionScope();
        short_.IsFailure.Should().BeTrue();
        short_.Error.Message.Should().Contain("must start its sandbox again");
        started.Should().Be(0);
        afterShort.ExportAttempts.Should().Be(0);
        ample.IsSuccess.Should().BeTrue(ample.IsFailure ? ample.Error.ToString() : "");
    }

    /// <summary>Red: mark ExportAttempts [property: JsonRequired]; a record written before it existed is unreadable.</summary>
    [Fact]
    public async Task A_record_written_before_the_restart_fields_reads_with_their_defaults()
    {
        var (provider, runId) = await ReadyRunAsync();
        var path = Path.Combine(DataRoot, "sandboxes", $"{runId:D}.json");
        var json = await File.ReadAllTextAsync(path);
        json.Should().Contain("\"exportAttempts\"").And.Contain("\"patchMissingReason\"");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("exportAttempts");
        node.Remove("patchMissingReason");
        var old = node.ToJsonString();
        await File.WriteAllTextAsync(path, old);

        var read = await provider.Store.ReadAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        old.Should().NotContain("exportAttempts").And.NotContain("patchMissingReason");
        read.Error.Should().BeNull();
        read.Record!.State.Should().Be(SandboxRecordState.Ready);
        read.Record.ExportAttempts.Should().Be(0);
        read.Record.PatchMissingReason.Should().BeNull();
    }

    // ---------- checkout hardening ----------

    /// <summary>Red: in ReadPublishableAsync, skip the PatchPath comparison; the checkout applies the derived patch and succeeds.</summary>
    [Fact]
    public async Task A_record_naming_a_foreign_patch_path_is_refused()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        (await provider.ParkAsync(runId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var record = await RecordOf(provider, runId);
        (await provider.Store.WriteAsync(record with { PatchPath = Path.Combine(_temp, "elsewhere.patch") }, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);

        using var _ = new AssertionScope();
        checkout.IsFailure.Should().BeTrue();
        checkout.Error.Message.Should().Contain("does not name its stored patch");
        (await Publish.FindAsync(runId, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Red: in LockRunAsync, give up at once when the lock is held, as a create does; the checkout fails while the park
    /// is still in progress.
    /// </summary>
    [Fact]
    public async Task A_checkout_waits_for_a_park_in_progress()
    {
        var (provider, runId) = await ReadyRunAsync();
        await File.WriteAllTextAsync(Path.Combine(RepoOf(runId), "Marker.cs"), Edited);
        var (entered, release) = BlockRemoval();

        var park = provider.ParkAsync(runId, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var checkout = provider.CheckoutForPublishAsync(runId, CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var waited = !checkout.IsCompleted;
        release.SetResult();
        var parked = await park.WaitAsync(TimeSpan.FromSeconds(60));
        var done = await checkout.WaitAsync(TimeSpan.FromSeconds(60));

        using var _ = new AssertionScope();
        waited.Should().BeTrue("the checkout waits while the park holds the run's lock");
        parked.IsSuccess.Should().BeTrue();
        done.IsSuccess.Should().BeTrue(done.IsFailure ? done.Error.ToString() : "");
        File.ReadAllText(Path.Combine(done.Value.Root, "Marker.cs")).Should().Be(Edited);
    }

    /// <summary>
    /// Red: in LockRunAsync, drop the OperationCanceledException catch; the bounded wait throws out of the checkout
    /// instead of returning a failure. Red 2: return the timeout as Validation again; the code differs.
    /// </summary>
    [Fact]
    public async Task A_lock_wait_that_runs_out_returns_a_failure()
    {
        var (provider, runId) = await ReadyRunAsync();
        provider.RunLockWait = TimeSpan.FromMilliseconds(300);
        var (entered, release) = BlockRemoval();
        var park = provider.ParkAsync(runId, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Result<RunWorkspace, AgentError>? checkout = null;
        var act = async () => checkout = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);
        await act.Should().NotThrowAsync();
        release.SetResult();
        await park.WaitAsync(TimeSpan.FromSeconds(60));

        using var _ = new AssertionScope();
        checkout!.Value.IsFailure.Should().BeTrue();
        checkout.Value.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        checkout.Value.Error.Message.Should().Contain("held by another call");
    }

    /// <summary>
    /// Red: in CheckoutForPublishAsync, release the lock with deleteFile false; the lock file the waiting checkout opened
    /// again outlives the removed run.
    /// </summary>
    [Fact]
    public async Task A_checkout_that_waited_on_a_remove_leaves_no_lock_file()
    {
        var (provider, runId) = await ReadyRunAsync();
        var (entered, release) = BlockRemoval();
        var remove = provider.RemoveAsync(runId, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var checkout = provider.CheckoutForPublishAsync(runId, CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        release.SetResult();
        var removed = await remove.WaitAsync(TimeSpan.FromSeconds(60));
        var done = await checkout.WaitAsync(TimeSpan.FromSeconds(60));

        using var _ = new AssertionScope();
        removed.IsSuccess.Should().BeTrue(removed.IsFailure ? removed.Error.ToString() : "");
        done.IsFailure.Should().BeTrue();
        File.Exists(provider.Store.LockPath(runId)).Should().BeFalse();
    }

    // ---------- harness ----------

    /// <summary>Makes the next removal notice wait, under the run's lock, until released; signals once it is waiting.</summary>
    private (TaskCompletionSource Entered, TaskCompletionSource Release) BlockRemoval()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _observer.BeforeRemoving = async () =>
        {
            _observer.BeforeRemoving = null;
            entered.SetResult();
            await release.Task;
        };
        return (entered, release);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_runtime is { } runtime)
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

    /// <summary>A provider with a run whose sandbox imported main and is ready.</summary>
    private async Task<(SandboxRunWorkspaceProvider Provider, Guid RunId)> ReadyRunAsync(Action<SandboxOptions>? configure = null)
    {
        var provider = Provider(configure);
        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(new RunWorkspaceRequest(runId, "repo", _remote.Url, "main", "run/feature", "App.slnx"), CancellationToken.None);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");
        var ready = await provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(2), CancellationToken.None);
        ready.IsSuccess.Should().BeTrue(ready.IsFailure ? ready.Error.ToString() : "");
        return (provider, runId);
    }

    private SandboxRunWorkspaceProvider Provider(Action<SandboxOptions>? configure = null)
    {
        _runtime ??= new FakeSandboxRuntime(Path.Combine(_temp, "sandboxes"), TimeProvider.System);
        var options = new SandboxOptions { DataRoot = DataRoot, Image = "thalos/sandbox:test" };
        options.ProtectedPaths.Add(".git/");
        options.ProtectedPaths.Add("AGENT.md");
        configure?.Invoke(options);
        var publish = new GitWorkspaceOptions { DataRoot = Path.Combine(DataRoot, "publish") };
        _publish = new SandboxPublishWorktrees(new GitWorktreeWorkspaceProvider(publish, [], NullLogger<GitWorktreeWorkspaceProvider>.Instance, TimeProvider.System));
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
            TimeProvider.System)
        {
            ReadyPollInterval = TimeSpan.FromMilliseconds(200),
        };
    }

    /// <summary>The checkout inside the run's in-process sandbox host: where agent-run code would write.</summary>
    private string RepoOf(Guid runId) => Path.Combine(Runtime.WorkRootOf(runId.ToString("N")), "repo");

    private static async Task<SandboxRecord> RecordOf(SandboxRunWorkspaceProvider provider, Guid runId)
    {
        var read = await provider.Store.ReadAsync(runId, CancellationToken.None);
        read.Record.Should().NotBeNull(read.Error ?? "the run has a record");
        return read.Record!;
    }

    /// <summary>Commits <paramref name="content"/> as <paramref name="name"/> on the remote's main.</summary>
    private void PushChange(string name, string content)
    {
        var clone = Path.Combine(_temp, "push-" + Guid.NewGuid().ToString("N"));
        Git(_temp, "clone", "--branch", "main", _remote.Url, clone);
        File.WriteAllText(Path.Combine(clone, name), content);
        Git(clone, "add", "--", name);
        Git(clone, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "change");
        Git(clone, "push", "origin", "main");
    }

    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
        return output;
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

        public IReadOnlyList<string> Events => [.. _events];

        /// <summary>Awaited before a removal notice is recorded; a test holds a park or remove under the run's lock with it.</summary>
        public Func<Task>? BeforeRemoving { get; set; }

        public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
        {
            _events.Enqueue($"ready {workspace.RunId}");
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
        {
            if (BeforeRemoving is { } before)
            {
                await before();
            }

            _events.Enqueue($"removing {workspace.RunId}");
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
