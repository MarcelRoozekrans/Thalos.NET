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

    private RunWorkspaceSweeper Sweeper() => new(_provider, _store, new FakeTimeProvider(Now), _log);

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
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
