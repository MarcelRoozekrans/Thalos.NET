using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// A named piece of host code a process node runs instead of an agent turn. A node declaring
/// <c>action: &lt;name&gt;</c> (<see cref="ProcessNode.Action"/>) is an action node: it names no <c>agent</c> or
/// <c>skill</c>, it declares <c>outcomes</c>, and the host action registered under that name decides which of them
/// the node finished with. The motivating case is a step no agent should be trusted to perform with its own tools,
/// such as opening a pull request from a run's workspace once the run's work has been approved.
/// </summary>
/// <remarks>
/// <para>
/// <b>A failure fails the run, the same way a rejected outcome does.</b> A failed <see cref="Result{T}"/> is an
/// expected, node-level failure: it is recorded through <see cref="IWorkflowStore.FailAsync"/>, naming the failing
/// node and this method's own error message. An <em>unexpected exception</em> escaping this method is different
/// and is not caught: like an exception from <see cref="ISubagentRunner.RunAsync"/> during a task node's turn, it
/// is left to propagate so the outbox retries the delivery. An action that wants a failure to be permanent must
/// return a failed <see cref="Result{T}"/>, not throw.
/// </para>
/// <para>
/// <b>An action must be idempotent.</b> It is called once per dispatch attempt, and a retried delivery calls it
/// again for the same node — including after an attempt whose side effect landed but whose transition was never
/// recorded. An action whose side effect is external, such as a pull request on a remote, must find and reuse what
/// an earlier attempt already made rather than make it twice.
/// </para>
/// <para>
/// <b>An <see cref="OperationCanceledException"/> out of this method means <c>ct</c> was cancelled — nothing
/// else.</b> It is not converted into a failed run: it propagates uncaught, exactly as an unexpected exception
/// does. That propagation is <em>not</em> the same as "the outbox will politely redeliver it": both the
/// ZeroAlloc.Outbox <c>OutboxWorkerService</c> and Daedalus's own outbox loop catch a dispatch failure
/// <c>when (ex is not OperationCanceledException)</c> — they treat cancellation as the loop's own shutdown signal,
/// never as "this message failed, retry it" — so an <see cref="OperationCanceledException"/> an action throws for
/// any other reason, for example an HTTP client's own request timeout surfacing as
/// <see cref="TaskCanceledException"/>, escapes both loops uncaught and can stop the worker outright, not merely get
/// this one message redelivered. An action must therefore never let its own timeout surface as
/// <see cref="OperationCanceledException"/>: only cancelling <c>ct</c> may produce one here. An action's own
/// timeout returns a failed <see cref="Result{T}"/> instead, the same as any other failure.
/// </para>
/// <para>
/// This is the same contract <see cref="IWorkflowDispatchGate"/> states for a gate, and for the same reasons; the
/// two are kept worded alike on purpose.
/// </para>
/// </remarks>
public interface IWorkflowHostAction
{
    /// <summary>
    /// The name a process node references with <c>action: &lt;name&gt;</c>, compared ordinally.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs the action for <paramref name="run"/>'s current node. A successful <see cref="Result{T}"/> carries an
    /// outcome, which must be one of <paramref name="node"/>'s declared <see cref="ProcessNode.Outcomes"/>, and
    /// variables to merge. A failed one fails the run with its <see cref="Result{T}.Error"/> message. It may run
    /// again for the same node on an outbox redelivery, so an action must be idempotent. See this interface's
    /// remarks for how a thrown exception and a cancelled <paramref name="ct"/> are handled instead.
    /// </summary>
    /// <param name="run">The run the action is for, positioned at the action node.</param>
    /// <param name="node">The action node being run — the node at <c>run.CurrentNode</c>.</param>
    /// <param name="ct">Cancellation for the dispatch itself, not for whatever the action started.</param>
    ValueTask<Result<HostActionResult>> RunAsync(WorkflowRun run, ProcessNode node, CancellationToken ct);
}
