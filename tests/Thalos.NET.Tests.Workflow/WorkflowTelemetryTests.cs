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
}
