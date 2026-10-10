using System.Diagnostics;
using System.Diagnostics.Metrics;
using Thalos.Runtime;

namespace Thalos.Workflow;

/// <summary>
/// The workflow engine's telemetry: the <c>thalos.workflow</c> <see cref="ActivitySource"/> and <see cref="Meter"/>,
/// the instruments the engine records on them itself, and the span and tag names it shares with the generated
/// <c>WorkflowStoreInstrumented</c> and <c>WorkflowHostActionInstrumented</c> proxies. Subscribe to
/// <see cref="SourceName"/> for both traces and metrics.
/// </summary>
/// <remarks>
/// <para>
/// <b>One trace per step.</b> Each node dispatch is its own trace, rooted at a <see cref="DispatchSpanName"/> span of kind
/// <see cref="ActivityKind.Consumer"/>, tagged <see cref="RunIdTag"/> and linked to the context that started the run,
/// <see cref="WorkflowRun.TraceParent"/>. A run waits at its gate and can outlive a restart, so no span stays open for
/// its whole life.
/// </para>
/// <para>
/// <b>No content.</b> Tags carry ids, names, counts, durations and outcomes only: never variables, prompt or agent text,
/// error messages or file content. A failed step marks its span with Error status and no description; the run's event
/// log keeps the error text.
/// </para>
/// </remarks>
public static class WorkflowTelemetry
{
    /// <summary>The source and meter name, <c>"thalos.workflow"</c>.</summary>
    public const string SourceName = ThalosTelemetry.WorkflowSourceName;

    /// <summary>The span each node dispatch starts.</summary>
    public const string DispatchSpanName = "thalos.workflow.dispatch";

    /// <summary>The span of <see cref="IWorkflowStore.StartAsync"/>, from the generated store proxy.</summary>
    public const string StoreStartSpanName = "thalos.workflow.store.start";

    /// <summary>The span of <see cref="IWorkflowStore.ResumeAsync"/>.</summary>
    public const string StoreResumeSpanName = "thalos.workflow.store.resume";

    /// <summary>The span of <see cref="IWorkflowStore.CancelAsync"/>.</summary>
    public const string StoreCancelSpanName = "thalos.workflow.store.cancel";

    /// <summary>The span of <see cref="IWorkflowStore.FailAsync"/>.</summary>
    public const string StoreFailSpanName = "thalos.workflow.store.fail";

    /// <summary>The span of <see cref="IWorkflowStore.FailStrandedAsync"/>.</summary>
    public const string StoreFailStrandedSpanName = "thalos.workflow.store.fail_stranded";

    /// <summary>The span of <see cref="IWorkflowStore.RetryFailedNodeAsync"/>.</summary>
    public const string StoreRetrySpanName = "thalos.workflow.store.retry";

    /// <summary>
    /// The span of a host action call, from the generated proxy. <c>{type}</c> is the wrapped action's type name, which the
    /// generator fills in; it is a literal token here.
    /// </summary>
    public const string HostActionSpanName = "thalos.workflow.host_action.{type}";

    /// <summary>The run's id, a <see cref="Guid"/>.</summary>
    public const string RunIdTag = "thalos.run.id";

    /// <summary>The node a dispatch or host action ran.</summary>
    public const string NodeTag = "thalos.node";

    /// <summary>The run's sequence number the dispatch was for, a <see cref="long"/>.</summary>
    public const string SeqTag = "thalos.seq";

    /// <summary>The run's process name.</summary>
    public const string ProcessTag = "thalos.run.process";

    /// <summary>How a dispatch ended: <see cref="OutcomeCompleted"/>, <see cref="OutcomeFailed"/>, <see cref="OutcomeAwaiting"/> or <see cref="OutcomeSkipped"/>.</summary>
    public const string OutcomeTag = "thalos.outcome";

    /// <summary>The terminal status a finished run reached: <c>Succeeded</c>, <c>Failed</c> or <c>Cancelled</c>.</summary>
    public const string RunStateTag = "thalos.run.state";

    /// <summary>The signal a resume carried.</summary>
    public const string SignalTag = "thalos.signal";

    /// <summary>The host action's name, as a process node references it.</summary>
    public const string HostActionTag = "thalos.host_action";

    /// <summary>Whether <see cref="IWorkflowStore.FailStrandedAsync"/> failed the run, a <see cref="bool"/>.</summary>
    public const string StrandedFailedTag = "thalos.stranded.failed";

    /// <summary>The full type name of an exception that escaped, as the OpenTelemetry conventions name it.</summary>
    public const string ErrorTypeTag = "error.type";

    /// <summary>The node ran and the run moved on, or reached a terminal node that succeeded.</summary>
    public const string OutcomeCompleted = "completed";

    /// <summary>The node failed, or reached a terminal node that failed.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>The node parked the run at its gate.</summary>
    public const string OutcomeAwaiting = "awaiting";

    /// <summary>The message was stale: the run was missing, past its seq, or no longer running.</summary>
    public const string OutcomeSkipped = "skipped";

    /// <summary>Counter name for started runs.</summary>
    public const string RunsStartedName = "thalos.workflow.runs.started";

    /// <summary>Counter name for runs that reached a terminal status.</summary>
    public const string RunsFinishedName = "thalos.workflow.runs.finished";

    /// <summary>Histogram name for one dispatch's duration, in milliseconds.</summary>
    public const string DispatchDurationName = "thalos.workflow.dispatch.duration";

    /// <summary>Histogram name for one host action call's duration, in milliseconds. Recorded by the generated proxy.</summary>
    public const string HostActionDurationName = "thalos.workflow.host_action.duration";

    /// <summary>The workflow engine's source.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Owner of the instruments below.</summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary><c>thalos.workflow.runs.started</c>, tagged <see cref="ProcessTag"/>.</summary>
    public static readonly Counter<long> RunsStarted =
        Meter.CreateCounter<long>(RunsStartedName, unit: "{run}", description: "Runs started, by process");

    /// <summary><c>thalos.workflow.runs.finished</c>, tagged <see cref="ProcessTag"/> and <see cref="RunStateTag"/>.</summary>
    public static readonly Counter<long> RunsFinished =
        Meter.CreateCounter<long>(RunsFinishedName, unit: "{run}", description: "Runs that reached Succeeded, Failed or Cancelled");

    /// <summary><c>thalos.workflow.dispatch.duration</c>, tagged <see cref="NodeTag"/> and <see cref="OutcomeTag"/>, or <see cref="ErrorTypeTag"/>.</summary>
    public static readonly Histogram<double> DispatchDuration =
        Meter.CreateHistogram<double>(DispatchDurationName, unit: "ms", description: "Duration of one node dispatch");

    /// <summary>Counts one run of <paramref name="process"/> started.</summary>
    internal static void RecordRunStarted(string process) =>
        RunsStarted.Add(1, new KeyValuePair<string, object?>(ProcessTag, process));

    /// <summary>Counts one run of <paramref name="process"/> that reached the terminal <paramref name="state"/>.</summary>
    internal static void RecordRunFinished(string process, WorkflowStatus state) =>
        RunsFinished.Add(
            1,
            new KeyValuePair<string, object?>(ProcessTag, process),
            new KeyValuePair<string, object?>(RunStateTag, StateName(state)));

    private static string StateName(WorkflowStatus state) => state switch
    {
        WorkflowStatus.Succeeded => nameof(WorkflowStatus.Succeeded),
        WorkflowStatus.Failed => nameof(WorkflowStatus.Failed),
        WorkflowStatus.Cancelled => nameof(WorkflowStatus.Cancelled),
        WorkflowStatus.Awaiting => nameof(WorkflowStatus.Awaiting),
        _ => nameof(WorkflowStatus.Running),
    };
}
