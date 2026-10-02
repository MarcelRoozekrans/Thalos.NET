using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Task A14: <see cref="RunWorkspaceSweeper"/> removes a workspace once its run no longer needs it, keeps a young
/// orphan through the grace period, reads each run right before its removal, and skips a failed removal without
/// abandoning the sweep — unless the sweep's own token was cancelled.
/// </summary>
public sealed class RunWorkspaceSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid OtherRunId = Guid.NewGuid();

    private readonly FakeRunWorkspaceProvider _provider = new();
    private readonly FakeWorkflowStore _store = new(new InMemoryProcessDefinitionStore());
    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _clock = new(Now);

    [Theory]
    [InlineData(WorkflowStatus.Failed, false, true)]
    [InlineData(WorkflowStatus.Cancelled, false, true)]
    [InlineData(WorkflowStatus.Failed, true, false)]      // failed after approval: kept
    [InlineData(WorkflowStatus.Cancelled, true, false)]   // cancelled after approval: kept
    [InlineData(WorkflowStatus.Awaiting, false, false)]
    [InlineData(WorkflowStatus.Running, false, false)]
    [InlineData(WorkflowStatus.Running, true, false)]     // resumed and publishing: kept
    [InlineData(WorkflowStatus.Succeeded, true, true)]    // published after approval: removed
    [InlineData(WorkflowStatus.Succeeded, false, true)]
    public async Task A_workspace_is_removed_once_its_run_no_longer_needs_it(WorkflowStatus status, bool resumed, bool removed)
    {
        var (sweeper, provider) = Seeded(status, resumed);
        await sweeper.SweepAsync(CancellationToken.None);
        provider.Removed.Contains(RunId).Should().Be(removed);
    }

    [Fact]
    public async Task A_workspace_with_no_run_row_older_than_the_grace_period_is_removed()
    {
        var (sweeper, provider) = Orphan(createdAt: Now - RunWorkspaceSweeper.OrphanGrace - TimeSpan.FromSeconds(1));
        (await sweeper.SweepAsync(CancellationToken.None)).Should().Be(1);
        provider.Removed.Should().Contain(RunId);
    }

    [Fact]
    public async Task A_workspace_with_no_run_row_inside_the_grace_period_is_kept()
    {
        var (sweeper, provider) = Orphan(createdAt: Now - TimeSpan.FromMinutes(1));   // the run is still being started
        await sweeper.SweepAsync(CancellationToken.None);
        provider.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workspace_with_no_run_row_exactly_the_grace_period_old_is_kept()
    {
        var (sweeper, provider) = Orphan(createdAt: Now - RunWorkspaceSweeper.OrphanGrace);
        await sweeper.SweepAsync(CancellationToken.None);
        provider.Removed.Should().BeEmpty("the grace period is a minimum: an orphan is removed only once it is older");
    }

    [Fact]
    public void The_grace_period_is_ten_minutes() =>
        RunWorkspaceSweeper.OrphanGrace.Should().Be(TimeSpan.FromMinutes(10));

    [Fact]
    public async Task A_failed_removal_is_skipped_and_the_sweep_goes_on()
    {
        var (sweeper, provider) = TwoEndedPreGateRuns(failRemovalForFirst: true);
        (await sweeper.SweepAsync(CancellationToken.None)).Should().Be(1);
        provider.Removed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_removal_is_logged_with_the_providers_reason()
    {
        var (sweeper, _) = TwoEndedPreGateRuns(failRemovalForFirst: true);
        await sweeper.SweepAsync(CancellationToken.None);

        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(RunId.ToString()).And.Contain("the fake refused the removal");
    }

    [Fact]
    public async Task A_removal_that_throws_is_logged_skipped_and_the_sweep_goes_on()
    {
        var (sweeper, provider) = TwoEndedPreGateRuns(failRemovalForFirst: false);
        var boom = new IOException("the worktree is in use");
        provider.BeforeRemoval = (runId, _) => runId == RunId ? throw boom : ValueTask.CompletedTask;

        var sweep = async () => await sweeper.SweepAsync(CancellationToken.None);

        (await sweep.Should().NotThrowAsync()).Subject.Should().Be(1);
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task A_run_that_cannot_be_read_is_logged_skipped_and_the_sweep_goes_on()
    {
        var (sweeper, provider) = TwoEndedPreGateRuns(failRemovalForFirst: false);
        _store.ThrowOnFindFor = RunId;

        var sweep = async () => await sweeper.SweepAsync(CancellationToken.None);

        (await sweep.Should().NotThrowAsync()).Subject.Should().Be(1);
        provider.Removed.Should().Contain(OtherRunId);
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancelling_the_sweep_propagates()
    {
        using var cts = new CancellationTokenSource();
        var (sweeper, provider) = Orphan(createdAt: Now - TimeSpan.FromHours(1));
        provider.BeforeRemoval = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };

        var sweep = async () => await sweeper.SweepAsync(cts.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_sweeps_own_is_a_failed_removal()
    {
        // An observer's own timeout, for instance, while the sweep's token is still live.
        var (sweeper, provider) = TwoEndedPreGateRuns(failRemovalForFirst: false);
        provider.BeforeRemoval = (runId, _) => runId == RunId ? throw new OperationCanceledException() : ValueTask.CompletedTask;

        var sweep = async () => await sweeper.SweepAsync(CancellationToken.None);

        (await sweep.Should().NotThrowAsync()).Subject.Should().Be(1);
    }

    [Fact]
    public async Task Each_run_is_read_right_before_its_own_removal_not_once_for_the_sweep()
    {
        // Two orphans past the grace period. While the first is being removed, the second's run row is written: a
        // sweep that read every run up front would still see the second as an orphan and remove a live run's
        // workspace.
        var sweeper = Sweeper();
        _provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        _provider.Add(Workspace(OtherRunId, Now - TimeSpan.FromHours(1)));
        _provider.BeforeRemoval = (runId, _) =>
        {
            if (runId == RunId)
            {
                _store.Seed(Run(OtherRunId, WorkflowStatus.Running, resumed: false));
            }

            return ValueTask.CompletedTask;
        };

        (await sweeper.SweepAsync(CancellationToken.None)).Should().Be(1);
        _provider.Removed.Should().BeEquivalentTo([RunId]);
    }

    // ---------- park (phase 2.6, A11) ----------

    /// <summary>
    /// Red: park only runs that are Running instead of every run that is not; the awaiting run is not parked. Red 2:
    /// remove a parked run's workspace; Removed holds the awaiting run.
    /// </summary>
    [Fact]
    public async Task An_awaiting_run_is_parked_and_kept()
    {
        var provider = new FakeParkableRunWorkspaceProvider();
        provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, WorkflowStatus.Awaiting, resumed: false));

        var removed = await Sweeper(provider).SweepAsync(CancellationToken.None);

        removed.Should().Be(0);
        provider.Parked.Should().Equal(RunId);
        provider.Removed.Should().BeEmpty();
    }

    /// <summary>Red: park every run, whatever its status; both running runs are parked.</summary>
    [Fact]
    public async Task A_running_run_is_never_parked()
    {
        var provider = new FakeParkableRunWorkspaceProvider();
        provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        provider.Add(Workspace(OtherRunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, WorkflowStatus.Running, resumed: false));
        _store.Seed(Run(OtherRunId, WorkflowStatus.Running, resumed: true));

        await Sweeper(provider).SweepAsync(CancellationToken.None);

        provider.Parked.Should().BeEmpty();
        provider.Removed.Should().BeEmpty();
    }

    /// <summary>Red: park a workspace whose run row does not exist; the young orphan is parked.</summary>
    [Fact]
    public async Task A_workspace_with_no_run_row_is_never_parked()
    {
        var provider = new FakeParkableRunWorkspaceProvider();
        provider.Add(Workspace(RunId, Now - TimeSpan.FromMinutes(1)));

        await Sweeper(provider).SweepAsync(CancellationToken.None);

        provider.Parked.Should().BeEmpty();
    }

    /// <summary>
    /// Red 1: drop the 912 log of a refused park; no warning names the run. Red 2: skip the removal decision after a
    /// failed park; the ended run's workspace is kept. Red 3: stop the sweep at a failed park; the other run is not parked.
    /// </summary>
    [Fact]
    public async Task A_failed_park_is_logged_and_the_sweep_goes_on()
    {
        var provider = new FakeParkableRunWorkspaceProvider { FailParkFor = RunId };
        provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        provider.Add(Workspace(OtherRunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, WorkflowStatus.Failed, resumed: false));
        _store.Seed(Run(OtherRunId, WorkflowStatus.Awaiting, resumed: false));

        var removed = await Sweeper(provider).SweepAsync(CancellationToken.None);

        removed.Should().Be(1);
        provider.Removed.Should().Equal(RunId);
        provider.Parked.Should().Equal(OtherRunId);
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Should().Match<(LogLevel Level, string Message, Exception? Exception, int EventId)>(e =>
                e.EventId == 912 && e.Message.Contains(RunId.ToString()) && e.Message.Contains("the fake could not export"));
    }

    /// <summary>Red: rethrow from the park step's catch; the exception escapes the sweep.</summary>
    [Fact]
    public async Task A_park_whose_run_cannot_be_read_is_logged_as_a_park_failure()
    {
        var provider = new FakeParkableRunWorkspaceProvider();
        provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, WorkflowStatus.Awaiting, resumed: false));
        _store.ThrowOnFindFor = RunId;

        var sweep = async () => await Sweeper(provider).SweepAsync(CancellationToken.None);

        await sweep.Should().NotThrowAsync();
        _log.Entries.Select(e => e.EventId).Should().Equal(912, 911);
    }

    /// <summary>
    /// Red: pass the sweep's own token to ParkAsync instead of the budget-bounded one; the first park hangs and the sweep
    /// never ends. Red 2: park whatever budget is left; the second hanging park is attempted too and no 913 is logged.
    /// Red 3: drop the 913 log; no warning says parks were left for the next sweep.
    /// </summary>
    [Fact]
    public async Task A_sweep_stops_parking_once_its_park_budget_is_spent()
    {
        var provider = new FakeParkableRunWorkspaceProvider
        {
            // A hanging export: the fake clock passes the budget, then the park waits until it is cancelled.
            BeforePark = async (_, ct) =>
            {
                _clock.Advance(TimeSpan.FromMinutes(2));
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        provider.Add(Workspace(OtherRunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, WorkflowStatus.Awaiting, resumed: false));
        _store.Seed(Run(OtherRunId, WorkflowStatus.Awaiting, resumed: false));
        var sweeper = new RunWorkspaceSweeper(provider, _store, _clock, _log) { ParkBudget = TimeSpan.FromMinutes(1) };

        var removed = await sweeper.SweepAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        removed.Should().Be(0);
        provider.ParkAttempts.Should().Equal(RunId);
        provider.Parked.Should().BeEmpty();
        _log.Entries.Select(e => e.EventId).Should().Equal(912, 913);
        _log.Entries[^1].Message.Should().Contain("1 workspaces were not parked");
    }

    [Fact]
    public void Every_dependency_is_required()
    {
        var logger = new CapturingLogger();
        var clock = new FakeTimeProvider(Now);

        FluentActions.Invoking(() => new RunWorkspaceSweeper(null!, _store, clock, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("workspaces");
        FluentActions.Invoking(() => new RunWorkspaceSweeper(_provider, null!, clock, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("store");
        FluentActions.Invoking(() => new RunWorkspaceSweeper(_provider, _store, null!, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("clock");
        FluentActions.Invoking(() => new RunWorkspaceSweeper(_provider, _store, clock, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    private (RunWorkspaceSweeper Sweeper, FakeRunWorkspaceProvider Provider) Seeded(WorkflowStatus status, bool resumed)
    {
        _provider.Add(Workspace(RunId, Now - TimeSpan.FromHours(1)));
        _store.Seed(Run(RunId, status, resumed));
        return (Sweeper(), _provider);
    }

    private (RunWorkspaceSweeper Sweeper, FakeRunWorkspaceProvider Provider) Orphan(DateTimeOffset createdAt)
    {
        _provider.Add(Workspace(RunId, createdAt));
        return (Sweeper(), _provider);
    }

    /// <summary>Two runs that failed before any gate, so both workspaces are removable; the first is listed first.</summary>
    private (RunWorkspaceSweeper Sweeper, FakeRunWorkspaceProvider Provider) TwoEndedPreGateRuns(bool failRemovalForFirst)
    {
        foreach (var id in new[] { RunId, OtherRunId })
        {
            _provider.Add(Workspace(id, Now - TimeSpan.FromHours(1)));
            _store.Seed(Run(id, WorkflowStatus.Failed, resumed: false));
        }

        if (failRemovalForFirst)
        {
            _provider.FailRemovalFor = RunId;
        }

        return (Sweeper(), _provider);
    }

    private RunWorkspaceSweeper Sweeper(IRunWorkspaceProvider? provider = null) => new(provider ?? _provider, _store, _clock, _log);

    private static RunWorkspace Workspace(Guid runId, DateTimeOffset createdAt) =>
        new(runId, "repo", "https://example.invalid/repo.git", "main", $"run/{runId}", $"/runs/{runId}", SolutionPath: null)
        {
            CreatedAt = createdAt,
        };

    private static WorkflowRun Run(Guid runId, WorkflowStatus status, bool resumed) => new()
    {
        Id = runId,
        Process = "manufacture",
        ProcessVersion = 1,
        CurrentNode = "somewhere",
        CurrentSeq = 3,
        Status = status,
        AwaitingSignal = status == WorkflowStatus.Awaiting ? "go" : null,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        StartedBy = TestPrincipals.Starter,
        LastResume = resumed ? new RunResume(new RunPrincipal("approver", ["admin"]), Now, "go") : null,
    };

    /// <summary>Records what the sweeper logged, so a test can assert a skipped removal is visible to an operator.</summary>
    private sealed class CapturingLogger : ILogger<RunWorkspaceSweeper>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, int EventId)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception, eventId.Id));
    }
}
