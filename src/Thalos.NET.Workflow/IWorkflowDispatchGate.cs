using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// A host-supplied check <see cref="WorkflowNodeDispatcher"/> runs immediately before a task node's agent turn —
/// the seam a host uses to make sure whatever that turn needs is actually there before paying for it. The
/// motivating case is a run's own tool servers: a host can start, or restart after a crash, the servers a node's
/// agent will call, and refuse the turn outright when they never come up, rather than dispatching an agent into a
/// turn that would fail on its first tool call anyway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only task nodes are gated.</b> <see cref="WorkflowNodeDispatcher.DispatchAsync"/> reaches
/// <see cref="BeforeTaskNodeAsync"/> only on the branch that is about to build a <see cref="SubagentRunRequest"/> —
/// never for a gate node (<see cref="ProcessNode.Await"/>) parking on an external signal, and never for a terminal
/// node. Neither spends a turn, so neither has anything for a gate to protect.
/// </para>
/// <para>
/// <b>A failure fails the run, the same way a rejected outcome does.</b> <see cref="WorkflowNodeDispatcher"/> never
/// throws for an expected, node-level failure — see its own remarks — and a gate that refuses is exactly that kind
/// of failure, not an infrastructure gap: it is recorded through <see cref="IWorkflowStore.FailAsync"/>, naming the
/// failing node and this method's own error message, and nothing is dispatched. An <em>unexpected exception</em>
/// escaping this method is different and is not caught here: like an exception from <see cref="ISubagentRunner.RunAsync"/>,
/// it is left to propagate so the outbox retries the delivery, because a gate's failure mode — a server that has not
/// started yet, a dependency that is momentarily unreachable — is transient in exactly the way a node-level
/// rejection is not. A gate that wants "refuse this turn" to be permanent must return a failed <see cref="Result"/>,
/// not throw.
/// </para>
/// <para>
/// <b>An <see cref="OperationCanceledException"/> out of this method means <c>ct</c> was cancelled — nothing
/// else.</b> It is not converted into a failed run: it propagates out of
/// <see cref="WorkflowNodeDispatcher.DispatchAsync"/> uncaught, exactly as an unexpected exception does. That
/// propagation is <em>not</em> the same as "the outbox will politely redeliver it": both the ZeroAlloc.Outbox
/// <c>OutboxWorkerService</c> and Daedalus's own outbox loop catch a dispatch failure
/// <c>when (ex is not OperationCanceledException)</c> — they treat cancellation as the loop's own shutdown
/// signal, never as "this message failed, retry it" — so an <see cref="OperationCanceledException"/> a gate
/// throws for any other reason, for example an HTTP client's own request timeout surfacing as
/// <see cref="TaskCanceledException"/>, escapes both loops uncaught and can stop the worker outright, not merely
/// get this one message redelivered. A gate must therefore never let its own timeout surface as
/// <see cref="OperationCanceledException"/>: only cancelling <c>ct</c> may produce one here. A gate's own timeout
/// returns a failed <see cref="Result"/> instead, the same as any other refusal.
/// </para>
/// <para>
/// Multiple gates run in registration order and short-circuit on the first failure — a gate after the failing one
/// is not called for this dispatch.
/// </para>
/// </remarks>
public interface IWorkflowDispatchGate
{
    /// <summary>
    /// Called once per dispatch attempt, immediately before <paramref name="run"/> dispatches
    /// <paramref name="node"/>'s agent turn — it may run again for the same node on an outbox redelivery, so a
    /// gate must be idempotent, the same as the turn it guards. A failed <see cref="Result"/> fails the run with
    /// its <see cref="Result.Error"/> message; nothing is dispatched. See this interface's remarks for how a
    /// thrown exception and a cancelled <paramref name="ct"/> are handled instead.
    /// </summary>
    /// <param name="run">The run about to dispatch, positioned at <paramref name="node"/>.</param>
    /// <param name="node">The task node about to run its agent turn — always <c>run.CurrentNode</c>.</param>
    /// <param name="ct">Cancellation for the dispatch itself, not for whatever the gate started.</param>
    ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct);
}
