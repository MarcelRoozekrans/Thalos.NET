using Thalos.Testing;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Daedalus and any OTLP dashboard subscribe to these exact names. Renaming one silently drops a signal downstream.
/// </summary>
public sealed class WorkflowTelemetryTests
{
    /// <summary>
    /// Red: rename the source or the meter, for example to <c>"thalos-workflow"</c>; the first two assertions fail.
    /// Red: rename any instrument; its assertion fails.
    /// Red: drop <c>unit: "ms"</c> from the dispatch histogram; the unit assertion fails.
    /// </summary>
    [Fact]
    public void The_source_meter_and_instruments_carry_the_documented_names()
    {
        WorkflowTelemetry.ActivitySource.Name.Should().Be("thalos.workflow");
        WorkflowTelemetry.Meter.Name.Should().Be("thalos.workflow");
        WorkflowTelemetry.RunsStarted.Name.Should().Be("thalos.workflow.runs.started");
        WorkflowTelemetry.RunsFinished.Name.Should().Be("thalos.workflow.runs.finished");
        WorkflowTelemetry.DispatchDuration.Name.Should().Be("thalos.workflow.dispatch.duration");
        WorkflowTelemetry.DispatchDuration.Unit.Should().Be("ms");
    }

    /// <summary>
    /// Red: swap the two counters in <c>RecordRunStarted</c> and <c>RecordRunFinished</c>; the filters find nothing.
    /// Red: change the added <c>1</c> to <c>2</c>; the value assertions fail.
    /// Red: drop the process tag; the process filter finds nothing.
    /// Red: add the state tag to <c>RecordRunStarted</c>; the started tag-count assertion fails.
    /// Red: change <c>unit: "{run}"</c> on either counter; its unit assertion fails.
    /// </summary>
    [Fact]
    public void Run_counters_record_one_with_the_process_and_state_tags()
    {
        var process = $"probe-{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        WorkflowTelemetry.RecordRunStarted(process);
        WorkflowTelemetry.RecordRunFinished(process, WorkflowStatus.Succeeded);

        var started = capture.MeasurementsOf(WorkflowTelemetry.RunsStartedName)
            .Where(m => Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.ProcessTag), process)).ToList();
        var finished = capture.MeasurementsOf(WorkflowTelemetry.RunsFinishedName)
            .Where(m => Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.ProcessTag), process)).ToList();
        started.Should().ContainSingle().Which.Value.Should().Be(1);
        started[0].Tags.Should().HaveCount(1);
        finished.Should().ContainSingle().Which.Value.Should().Be(1);
        finished[0].Tags.Should().HaveCount(2);
        finished[0].Tags[WorkflowTelemetry.RunStateTag].Should().Be("Succeeded");
        WorkflowTelemetry.RunsStarted.Unit.Should().Be("{run}");
        WorkflowTelemetry.RunsFinished.Unit.Should().Be("{run}");
    }

    /// <summary>
    /// Red: change a terminal arm of <c>StateName</c> to another name; its row fails.
    /// </summary>
    [Theory]
    [InlineData(WorkflowStatus.Succeeded, "Succeeded")]
    [InlineData(WorkflowStatus.Failed, "Failed")]
    [InlineData(WorkflowStatus.Cancelled, "Cancelled")]
    public void A_finished_run_is_tagged_with_its_terminal_state(WorkflowStatus status, string expected)
    {
        var process = $"probe-{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        WorkflowTelemetry.RecordRunFinished(process, status);

        capture.MeasurementsOf(WorkflowTelemetry.RunsFinishedName)
            .Single(m => Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.ProcessTag), process))
            .Tags[WorkflowTelemetry.RunStateTag].Should().Be(expected);
    }

    /// <summary>
    /// Dashboards and Daedalus match these literals. Red: change any one constant; its assertion fails.
    /// </summary>
    [Fact]
    public void The_span_tag_outcome_and_metric_constants_carry_the_literal_values_dashboards_use()
    {
        WorkflowTelemetry.SourceName.Should().Be("thalos.workflow");
        WorkflowTelemetry.DispatchSpanName.Should().Be("thalos.workflow.dispatch");
        WorkflowTelemetry.StoreStartSpanName.Should().Be("thalos.workflow.store.start");
        WorkflowTelemetry.StoreResumeSpanName.Should().Be("thalos.workflow.store.resume");
        WorkflowTelemetry.StoreCancelSpanName.Should().Be("thalos.workflow.store.cancel");
        WorkflowTelemetry.StoreFailSpanName.Should().Be("thalos.workflow.store.fail");
        WorkflowTelemetry.StoreFailStrandedSpanName.Should().Be("thalos.workflow.store.fail_stranded");
        WorkflowTelemetry.StoreRetrySpanName.Should().Be("thalos.workflow.store.retry");
        WorkflowTelemetry.HostActionSpanName.Should().Be("thalos.workflow.host_action.{type}");
        WorkflowTelemetry.RunIdTag.Should().Be("thalos.run.id");
        WorkflowTelemetry.NodeTag.Should().Be("thalos.node");
        WorkflowTelemetry.SeqTag.Should().Be("thalos.seq");
        WorkflowTelemetry.ProcessTag.Should().Be("thalos.run.process");
        WorkflowTelemetry.OutcomeTag.Should().Be("thalos.outcome");
        WorkflowTelemetry.RunStateTag.Should().Be("thalos.run.state");
        WorkflowTelemetry.SignalTag.Should().Be("thalos.signal");
        WorkflowTelemetry.HostActionTag.Should().Be("thalos.host_action");
        WorkflowTelemetry.StrandedFailedTag.Should().Be("thalos.stranded.failed");
        WorkflowTelemetry.ErrorTypeTag.Should().Be("error.type");
        WorkflowTelemetry.OutcomeCompleted.Should().Be("completed");
        WorkflowTelemetry.OutcomeFailed.Should().Be("failed");
        WorkflowTelemetry.OutcomeAwaiting.Should().Be("awaiting");
        WorkflowTelemetry.OutcomeSkipped.Should().Be("skipped");
        WorkflowTelemetry.RunsStartedName.Should().Be("thalos.workflow.runs.started");
        WorkflowTelemetry.RunsFinishedName.Should().Be("thalos.workflow.runs.finished");
        WorkflowTelemetry.DispatchDurationName.Should().Be("thalos.workflow.dispatch.duration");
        WorkflowTelemetry.HostActionDurationName.Should().Be("thalos.workflow.host_action.duration");
    }
}
