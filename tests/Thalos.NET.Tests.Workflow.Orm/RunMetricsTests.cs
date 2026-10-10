using Thalos.Testing;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// 0.15.0, spec Part A step 5: runs started and finished are counted where the store commits them, once per
/// transition. Each test uses a process name of its own, so the measurements can be filtered on it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class RunMetricsTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        _store = new OrmWorkflowStore(options, new OrmProcessDefinitionStore(options));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Red: record before the <c>inserted == 0</c> check; the repeat counts too, and the count is 2.
    /// Red: tag the correlation key in place of the process; the filter finds nothing.
    /// </summary>
    [Fact]
    public async Task A_new_run_counts_once_and_a_repeated_start_does_not()
    {
        var process = Process();
        var request = Request(process, "c-once");
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await _store.StartAsync(request, CancellationToken.None);
        await _store.StartAsync(request, CancellationToken.None);

        Of(capture, WorkflowTelemetry.RunsStartedName, process).Should().ContainSingle().Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Red: drop the record in <c>CancelAsync</c>; nothing is counted.
    /// Red: record before the terminal check; the second cancel, a no-op, counts again.
    /// Red: tag <c>Failed</c> in <c>CancelAsync</c>; the state assertion fails.
    /// </summary>
    [Fact]
    public async Task A_cancel_counts_one_cancelled_run_and_a_repeated_cancel_does_not()
    {
        var process = Process();
        var runId = (await _store.StartAsync(Request(process, "c-cancel"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await _store.CancelAsync(runId, "test", CancellationToken.None);
        await _store.CancelAsync(runId, "test", CancellationToken.None);

        Of(capture, WorkflowTelemetry.RunsFinishedName, process).Should().ContainSingle()
            .Which.Tags[WorkflowTelemetry.RunStateTag].Should().Be(nameof(WorkflowStatus.Cancelled));
    }

    /// <summary>Red: drop the record in <c>FailAsync</c>; nothing is counted.</summary>
    [Fact]
    public async Task A_fail_counts_one_failed_run()
    {
        var process = Process();
        var runId = (await _store.StartAsync(Request(process, "c-fail"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await _store.FailAsync(runId, "test", CancellationToken.None);

        Of(capture, WorkflowTelemetry.RunsFinishedName, process).Should().ContainSingle()
            .Which.Tags[WorkflowTelemetry.RunStateTag].Should().Be(nameof(WorkflowStatus.Failed));
    }

    /// <summary>
    /// Red: drop the record in <c>FailStrandedAsync</c>; nothing is counted.
    /// Red: record when it returns false; the stale-seq call counts too.
    /// </summary>
    [Fact]
    public async Task A_stranded_fail_counts_only_when_it_fails_the_run()
    {
        var process = Process();
        var runId = (await _store.StartAsync(Request(process, "c-stranded"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        (await _store.FailStrandedAsync(runId, 7, "stale", CancellationToken.None)).Should().BeFalse();
        (await _store.FailStrandedAsync(runId, 1, "stranded", CancellationToken.None)).Should().BeTrue();

        Of(capture, WorkflowTelemetry.RunsFinishedName, process).Should().ContainSingle()
            .Which.Tags[WorkflowTelemetry.RunStateTag].Should().Be(nameof(WorkflowStatus.Failed));
    }

    /// <summary>
    /// Red: drop the terminal-status record in <c>CompleteNodeAsync</c>; the Succeeded count is missing.
    /// Red: record on every completion; the Running step is counted too.
    /// </summary>
    [Fact]
    public async Task A_completion_counts_only_the_one_that_ends_the_run()
    {
        var process = Process();
        var runId = (await _store.StartAsync(Request(process, "c-complete"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await _store.CompleteNodeAsync(runId, 1, new WorkflowTransition("done", WorkflowStatus.Running, null, WorkflowEventKind.Completed), new NodeResult("ok", Empty), CancellationToken.None);
        await _store.CompleteNodeAsync(runId, 2, new WorkflowTransition("done", WorkflowStatus.Succeeded, null, WorkflowEventKind.Completed), new NodeResult(null, Empty), CancellationToken.None);

        Of(capture, WorkflowTelemetry.RunsFinishedName, process).Should().ContainSingle()
            .Which.Tags[WorkflowTelemetry.RunStateTag].Should().Be(nameof(WorkflowStatus.Succeeded));
    }

    private static string Process() => $"probe-metrics-{Guid.NewGuid():N}";

    private static IReadOnlyList<CapturedMeasurement> Of(TelemetryCapture capture, string instrument, string process) =>
        [.. capture.MeasurementsOf(instrument).Where(m => process.Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.ProcessTag)))];

    private static WorkflowStartRequest Request(string process, string correlationKey) => new()
    {
        Process = process,
        Version = 1,
        CorrelationKey = $"{correlationKey}-{Guid.NewGuid():N}",
        StartNode = "implement",
        InitialVariables = null,
        StartedBy = TestPrincipals.Starter,
    };
}
