using System.Diagnostics.Metrics;
using Thalos.Runtime;
using Thalos.Workspaces;

namespace Thalos.Mcp;

/// <summary>
/// Records <c>thalos.workflow.run_tool.duration</c> (ms), tagged <c>thalos.tool.source</c> and <c>thalos.tool</c>, for
/// every call a remote run tool source sent to a run's endpoint, sandbox <c>build</c> and <c>test</c> included. It reads
/// <see cref="RunToolCall.Elapsed"/>, which the source measured around the call. It is told only after the call
/// finished, so it records a duration and starts no span. It never reads <see cref="RunToolCall.ResultText"/>.
/// </summary>
/// <remarks>
/// The meter is named <see cref="ThalosTelemetry.WorkflowSourceName"/>, the workflow engine's. This assembly does not
/// reference the engine, so it owns its own <see cref="Meter"/> instance under that name; a listener subscribes by name.
/// </remarks>
internal sealed class RunToolDurationObserver : IRunToolCallObserver
{
    internal const string InstrumentName = "thalos.workflow.run_tool.duration";

    internal const string SourceTag = "thalos.tool.source";

    internal const string ToolTag = "thalos.tool";

    private static readonly Meter Meter = new(ThalosTelemetry.WorkflowSourceName);

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(InstrumentName, unit: "ms", description: "Duration of one run tool call");

    /// <inheritdoc/>
    public ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completed);
        Duration.Record(
            completed.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(SourceTag, completed.Source),
            new KeyValuePair<string, object?>(ToolTag, completed.Tool));
        return ValueTask.CompletedTask;
    }
}
