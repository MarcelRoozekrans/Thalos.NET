using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Fix round 4: cancellation cleanup, whole-file claim publication, the provisional-record guard on remove, the
/// recursive emptying of the isolation directories, and first clones racing across provider instances. Every
/// provider instance here stands in for a separate host process sharing one DataRoot: separate instances share no
/// in-process lock, exactly like an API host and a CLI host.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    /// <summary>
    /// Cancelling a create while its first clone is still running must propagate as
    /// <see cref="OperationCanceledException"/> and still undo everything the create claimed: no sidecar, no claim
    /// temp file, no temporary clone directory. Before round 4 the claimed sidecar stayed behind with no mirror, so a
    /// later create for the same run failed with "already exists".
    /// </summary>
    [Fact]
    public async Task Cancelling_a_create_during_its_first_clone_removes_its_claim_and_a_later_create_succeeds()
    {
        using var remote = LocalGitRemote.Create();
        var hangingCloneGit = WriteHangingCloneGit(_temp);
        var runId = Guid.NewGuid();
        var hangingProvider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = hangingCloneGit },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var create = hangingProvider.CreateAsync(Request(remote, runId), cts.Token).AsTask();
        await WaitForCloneToStartAsync(create);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => create).Should().ThrowAsync<OperationCanceledException>();

        File.Exists(SidecarPath(runId)).Should().BeFalse("a cancelled create must delete the sidecar it claimed");
        Directory.EnumerateFiles(Path.Combine(_dataRoot, "runs"), "*.tmp").Should().BeEmpty("no claim temp file may outlive a cancelled create");
        Directory.EnumerateDirectories(Path.Combine(_dataRoot, "mirrors"), ".tmp-*").Should().BeEmpty("the temporary clone directory must go too");
        var again = await Provider(out _).CreateAsync(Request(remote, runId), CancellationToken.None);
        again.IsSuccess.Should().BeTrue("nothing the cancelled create claimed may block a later create for the same run");
    }

    /// <summary>
    /// A record whose mirror is gone has nothing left to remove on the git side. Before round 4, git was started in
    /// the missing mirror directory, and <see cref="System.ComponentModel.Win32Exception"/> escaped every remove.
    /// </summary>
    [Fact]
    public async Task Remove_succeeds_and_clears_the_record_when_the_mirror_is_gone()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out _);
        var ws = (await provider.CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;
        DeleteTree(MirrorOf("sandbox"));

        var result = await provider.RemoveAsync(ws.RunId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a missing mirror means nothing to remove on the git side");
        File.Exists(SidecarPath(ws.RunId)).Should().BeFalse("the record must be cleared, or the sweeper retries it forever");
        Directory.Exists(ws.Root).Should().BeFalse("the orphaned worktree directory must be removed too");
    }

    /// <summary>
    /// A provisional record belongs to a create still in flight, possibly in another process. Remove refuses it
    /// until it is older than the R9 grace period; after that the claimant is presumed dead and remove proceeds.
    /// Find does not report a record that is still being created.
    /// </summary>
    [Fact]
    public async Task Remove_refuses_a_fresh_provisional_record_and_accepts_one_older_than_the_grace_period()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = Provider(out _, clock);
        var runId = Guid.NewGuid();
        var request = new RunWorkspaceRequest(runId, "sandbox", "https://example.invalid/repo.git", "main", $"manufacture/{runId}", null);
        (await provider.ClaimAsync(request, Path.Combine(_dataRoot, "runs", runId.ToString()), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await provider.FindAsync(runId, CancellationToken.None)).Should().BeNull("a record still being created is not a workspace yet");
        var fresh = await provider.RemoveAsync(runId, CancellationToken.None);
        fresh.IsFailure.Should().BeTrue("a remove must never pull an in-flight create's workspace out from under it");
        File.Exists(SidecarPath(runId)).Should().BeTrue("a refused remove leaves the in-flight claim alone");

        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await provider.RemoveAsync(runId, CancellationToken.None);
        stale.IsSuccess.Should().BeTrue("a provisional record older than the grace period belongs to a dead claimant");
        File.Exists(SidecarPath(runId)).Should().BeFalse();
    }

    /// <summary>
    /// A claim is published whole, so a reader racing it sees either no record or the complete one — never a
    /// sharing violation, never a half-written file. Before round 4 the claim wrote the sidecar in place:
    /// review saw <see cref="IOException"/> and <see cref="System.Text.Json.JsonException"/> escape Find and Remove
    /// hundreds of times over 400 trials. The unreadable-record count catches a partial read even where a reader
    /// tolerates it rather than throwing.
    /// </summary>
    [Fact]
    public async Task Claims_racing_find_remove_and_list_never_throw_and_never_expose_a_partial_record()
    {
        const int trials = 300;
        var logger = new CountingLogger();
        var claimant = new GitWorktreeWorkspaceProvider(new GitWorkspaceOptions { DataRoot = _dataRoot }, [], logger, TimeProvider.System);
        var reader = new GitWorktreeWorkspaceProvider(new GitWorkspaceOptions { DataRoot = _dataRoot }, [], logger, TimeProvider.System);
        var exceptions = new ConcurrentBag<Exception>();
        var unexpectedRemoveFailures = new ConcurrentBag<string>();

        for (var trial = 0; trial < trials; trial++)
        {
            var runId = Guid.NewGuid();
            var request = new RunWorkspaceRequest(runId, "sandbox", "https://example.invalid/repo.git", "main", $"manufacture/{runId}", null);
            var root = Path.Combine(_dataRoot, "runs", runId.ToString());
            var barrier = new Barrier(5);
            await Task.WhenAll(
                RaceAsync(barrier, exceptions, async () => (await claimant.ClaimAsync(request, root, CancellationToken.None)).IsSuccess.Should().BeTrue()),
                RaceAsync(barrier, exceptions, async () => await reader.FindAsync(runId, CancellationToken.None)),
                RaceAsync(barrier, exceptions, async () => await reader.ListAsync(CancellationToken.None)),
                RaceAsync(barrier, exceptions, async () => RecordUnexpected(await reader.RemoveAsync(runId, CancellationToken.None), unexpectedRemoveFailures)),
                RaceAsync(barrier, exceptions, async () => RecordUnexpected(await reader.RemoveAsync(runId, CancellationToken.None), unexpectedRemoveFailures)));

            // Keeps each trial's ListAsync reading one record, not every earlier trial's too.
            File.Delete(SidecarPath(runId));
        }

        exceptions.Should().BeEmpty("no public method may throw while a claim is being published");
        logger.UnreadableSidecars.Should().Be(0, "a reader must only ever see no record or a complete one");
        unexpectedRemoveFailures.Should().BeEmpty("a racing remove may only find nothing or refuse the in-flight claim");
    }

    /// <summary>
    /// A claimant that crashed mid-publish leaves only its own uniquely named temp file, never a partial sidecar.
    /// The sweep entry point, <see cref="GitWorktreeWorkspaceProvider.ListAsync"/>, ignores such a file as a record,
    /// deletes it once it is older than the grace period, and leaves a fresh one alone, since a fresh one may belong to
    /// a claim still in progress.
    /// </summary>
    [Fact]
    public async Task A_crashed_claims_leftover_temp_file_is_swept_once_stale_and_never_read_as_a_record()
    {
        var provider = Provider(out _);
        var runsDir = Path.Combine(_dataRoot, "runs");
        var stale = Path.Combine(runsDir, $".{Guid.NewGuid():N}.{Guid.NewGuid():N}.sidecar.tmp");
        var fresh = Path.Combine(runsDir, $".{Guid.NewGuid():N}.{Guid.NewGuid():N}.sidecar.tmp");

        // The fresh temp holds a complete, valid record — the one a real claim publishes — so only its name keeps
        // it from being read as a record. The stale one is what a crash mid-write leaves: part of a record.
        var claimedRunId = Guid.NewGuid();
        var request = new RunWorkspaceRequest(claimedRunId, "sandbox", "https://example.invalid/repo.git", "main", $"manufacture/{claimedRunId}", null);
        (await provider.ClaimAsync(request, Path.Combine(runsDir, claimedRunId.ToString()), CancellationToken.None)).IsSuccess.Should().BeTrue();
        File.Move(SidecarPath(claimedRunId), fresh);
        File.WriteAllText(stale, "{\"State\":\"Provisional\",\"Works");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        var listed = await provider.ListAsync(CancellationToken.None);

        listed.Should().BeEmpty("a temp file is never a record, however complete its content");
        File.Exists(stale).Should().BeFalse("a stale claim temp file must be swept");
        File.Exists(fresh).Should().BeTrue("a fresh temp file may belong to a claim still being published");
    }

    /// <summary>
    /// Git reads <c>$XDG_CONFIG_HOME/git/attributes</c> and <c>ignore</c>, which <c>GIT_CONFIG_GLOBAL</c> does not
    /// cover, and <c>~/.netrc</c>. Review planted a nested <c>home/git/attributes</c> that survived 150
    /// constructions, and git honoured it; a read-only <c>.netrc</c> survived silently on Windows. Both must be gone
    /// after construction, along with anything nested under the hooks directory or inside a read-only directory.
    /// </summary>
    [Fact]
    public void Planted_nested_and_read_only_files_under_the_isolation_directories_are_gone_after_construction()
    {
        var isolation = Path.Combine(_dataRoot, ".git-isolation");
        var attributes = PlantFile(Path.Combine(isolation, "home", "git", "attributes"), "* filter=planted\n");
        var netrc = PlantFile(Path.Combine(isolation, "home", ".netrc"), "machine example.invalid login x password y\n");
        File.SetAttributes(netrc, FileAttributes.ReadOnly);
        var nestedHook = PlantFile(Path.Combine(isolation, "hooks", "nested", "post-checkout"), "#!/bin/sh\n");
        var readOnlyDir = Path.Combine(isolation, "home", "locked");
        var insideReadOnlyDir = PlantFile(Path.Combine(readOnlyDir, "ignore"), "*\n");
        File.SetAttributes(readOnlyDir, FileAttributes.Directory | FileAttributes.ReadOnly);

        _ = Provider(out _);

        File.Exists(attributes).Should().BeFalse("a nested planted attributes file must not survive construction");
        File.Exists(netrc).Should().BeFalse("a read-only planted .netrc must not survive construction");
        File.Exists(nestedHook).Should().BeFalse("a nested planted hook must not survive construction");
        File.Exists(insideReadOnlyDir).Should().BeFalse("a file inside a read-only planted directory must not survive construction");
        Directory.EnumerateFileSystemEntries(Path.Combine(isolation, "home")).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(isolation, "hooks")).Should().BeEmpty();
    }

    /// <summary>
    /// A planted file the constructor cannot delete must fail construction, never be skipped silently. Windows only:
    /// an open handle without delete sharing blocks deletion there, while Linux unlinks an open file regardless, so
    /// there is no equivalent undeletable file for a root test process on Linux.
    /// </summary>
    [SkippableFact]
    public void A_planted_isolation_file_that_cannot_be_deleted_fails_construction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Linux unlinks an open file, so this blocker only exists on Windows.");
        var netrc = PlantFile(Path.Combine(_dataRoot, ".git-isolation", "home", ".netrc"), "machine example.invalid login x password y\n");

        using (new FileStream(netrc, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FluentActions.Invoking(() => Provider(out _)).Should().Throw<Exception>("git must never run with a planted home file it could read");
        }
    }

    /// <summary>
    /// The isolation directories must stay race-free under 150 parallel constructions on one DataRoot even when
    /// there is planted nested content for them all to delete at once, and nothing planted may survive.
    /// </summary>
    [Fact]
    public async Task Constructing_150_providers_concurrently_over_planted_nested_files_never_throws_and_leaves_nothing()
    {
        const int constructions = 150;
        var isolation = Path.Combine(_dataRoot, ".git-isolation");
        for (var i = 0; i < 20; i++)
        {
            PlantFile(Path.Combine(isolation, "home", $"dir{i}", "git", "attributes"), "* filter=planted\n");
            PlantFile(Path.Combine(isolation, "hooks", $"dir{i}", "post-checkout"), "#!/bin/sh\n");
        }

        var exceptions = new ConcurrentBag<Exception>();
        var barrier = new Barrier(constructions);
        var tasks = new Task[constructions];
        for (var i = 0; i < constructions; i++)
        {
            // A dedicated thread per constructor: 150 threads blocked on one Barrier would starve the thread pool.
            tasks[i] = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    ConstructProviderCatching(exceptions);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("concurrent construction over planted content must never throw");
        Directory.EnumerateFileSystemEntries(Path.Combine(isolation, "home")).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(isolation, "hooks")).Should().BeEmpty();
    }

    /// <summary>
    /// Six provider instances, each standing in for a separate host process, create workspaces for six different runs
    /// against one repository none of them has cloned yet. Before round 4, an <see cref="IOException"/> from
    /// <see cref="Directory.Move(string, string)"/> escaped in 5 of 6 attempts, when a first clone lost the final move
    /// to another's, and left the claimed sidecars behind. With that fixed, git's own lock files still failed some of
    /// the racing creates — "could not lock config file" — because git refuses a concurrent second writer rather than
    /// waiting, so git on a mirror is now serialised across processes too. Every create must succeed, with a real
    /// checkout of main.
    /// </summary>
    [Fact]
    public async Task Six_providers_racing_first_clones_of_one_new_repository_all_succeed()
    {
        const int trials = 3;
        const int racers = 6;
        for (var trial = 0; trial < trials; trial++)
        {
            using var remote = LocalGitRemote.Create();
            var repository = $"fresh-{trial}";
            var exceptions = new ConcurrentBag<Exception>();
            var results = new ConcurrentBag<Result<RunWorkspace, AgentError>>();
            var barrier = new Barrier(racers);
            var tasks = new Task[racers];
            for (var i = 0; i < racers; i++)
            {
                var provider = Provider(out _);
                var runId = Guid.NewGuid();
                var request = new RunWorkspaceRequest(runId, repository, remote.Url, "main", $"manufacture/{runId}", null);
                tasks[i] = RaceAsync(barrier, exceptions, async () => results.Add(await provider.CreateAsync(request, CancellationToken.None)));
            }

            await Task.WhenAll(tasks);

            exceptions.Should().BeEmpty($"trial {trial}: every racing first clone must return a Result");
            results.Should().OnlyContain(r => r.IsSuccess, $"trial {trial}: no create may fail because another host was running git on the same mirror");
            foreach (var result in results)
            {
                Git(result.Value.Root, "rev-parse HEAD").Should().Be(remote.HeadOf("main"), $"trial {trial}: every workspace must be a real checkout of main");
            }
        }
    }

    /// <summary>A runs path that is a file, not a directory, fails the create with a Result that says so.</summary>
    [Fact]
    public async Task A_runs_path_that_is_not_a_directory_fails_the_create_with_an_accurate_result()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "runs"), "not a directory");
        var runId = Guid.NewGuid();

        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", "https://example.invalid/repo.git", "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.ToString().Should().Contain("not a directory");
    }

    /// <summary>
    /// Waits until the hanging fake git has created its clone destination, so cancelling lands after the claim and
    /// inside the clone. Fails fast if the create finished early instead.
    /// </summary>
    private async Task WaitForCloneToStartAsync(Task create)
    {
        var mirrorsDir = Path.Combine(_dataRoot, "mirrors");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!CloneHasStarted(mirrorsDir))
        {
            create.IsCompleted.Should().BeFalse("the create must still be inside its hanging clone when it is cancelled");
            DateTime.UtcNow.Should().BeBefore(deadline, "the fake git never started its clone");
            await Task.Delay(50);
        }
    }

    private static bool CloneHasStarted(string mirrorsDir) =>
        Directory.Exists(mirrorsDir) && Directory.EnumerateDirectories(mirrorsDir, ".tmp-*").Any();

    /// <summary>
    /// Runs <paramref name="action"/> on a thread-pool thread once every racer reaches <paramref name="barrier"/>,
    /// recording any exception instead of letting it end the test before the others finish.
    /// <see cref="Task.Run(Func{Task})"/> creates real concurrency here; it does not fake async over a sync API.
    /// </summary>
    private static Task RaceAsync(Barrier barrier, ConcurrentBag<Exception> exceptions, Func<Task> action) =>
        Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

    /// <summary>
    /// Records a remove failure other than the provisional-record refusal: during a claim race, a remove may find no
    /// record or refuse the in-flight one, and anything else means it saw something it should not have.
    /// </summary>
    private static void RecordUnexpected(UnitResult<AgentError> result, ConcurrentBag<string> unexpected)
    {
        if (result.IsFailure && !result.Error.ToString().Contains("still being created", StringComparison.Ordinal))
        {
            unexpected.Add(result.Error.ToString());
        }
    }

    private static string PlantFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Deletes a tree git created, clearing the read-only attribute git sets on object files first.</summary>
    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Counts the provider's unreadable-sidecar warnings, event 1002, from any reader.</summary>
    private sealed class CountingLogger : ILogger<GitWorktreeWorkspaceProvider>
    {
        private int _unreadableSidecars;

        public int UnreadableSidecars => Volatile.Read(ref _unreadableSidecars);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1002)
            {
                Interlocked.Increment(ref _unreadableSidecars);
            }
        }
    }
}
