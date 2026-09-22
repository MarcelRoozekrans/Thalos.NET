using System.Text.Json;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Runs the agent turn a <see cref="WorkflowRun"/>'s current node calls for, then hands the result to
/// <see cref="WorkflowInterpreter.Advance"/> and persists whatever it decides through <see cref="IWorkflowStore"/>.
/// This is the engine's one seam onto a live agent: it depends on <see cref="ISubagentRunner"/> only — no
/// principal, no roles, no host-specific authorization — the same direction as
/// <c>Thalos.Git.IPullRequestPublisher</c>, where Thalos states the capability it needs and a host supplies the
/// host-specific part. Here that part is <c>resolveCaller</c>: an opaque function from a run to the
/// <see cref="ISecurityContext"/> it executes as, supplied by whoever registers this dispatcher. This dispatcher
/// never inspects or interprets what comes back from it — it only forwards the value into
/// <see cref="SubagentRunRequest.Caller"/>. Agent name resolution is the same shape: <see cref="IWorkflowReferenceResolver"/>
/// is a host-backed port (its <c>ResolveAgentIdAsync</c>), not something this dispatcher implements itself.
/// </summary>
/// <remarks>
/// <b>A node failure must not throw.</b> A throw would hand the message back to the outbox for eight retries with
/// exponential backoff, re-running the subagent each time — paying for the same failing turn eight times over,
/// with nobody told until it dead-letters. A result this dispatcher dislikes — the turn itself failed, an
/// unresolvable agent name, an ambiguous or missing outcome, or <see cref="WorkflowInterpreter.Advance"/> rejecting
/// the outcome it produced — is recorded through <see cref="IWorkflowStore.FailAsync"/> instead, which marks the
/// run <see cref="WorkflowStatus.Failed"/> without leaving the outbox anything to retry. Only a turn that never
/// produced a result at all — an unexpected exception escaping <see cref="ISubagentRunner.RunAsync"/> itself, or
/// <see cref="IWorkflowStore"/> throwing <see cref="WorkflowConcurrencyException"/> because another dispatch
/// already won the race to complete this node — is allowed to propagate, because those genuinely are the
/// transient, infrastructure-shaped failures a retry can fix.
/// </remarks>
public sealed class WorkflowNodeDispatcher(
    IWorkflowStore store,
    ISubagentRunner runner,
    IWorkflowReferenceResolver resolver,
    IReadOnlyDictionary<(string Process, int Version), ProcessDefinition> processes,
    Func<WorkflowRun, ISecurityContext> resolveCaller)
{
    /// <summary>
    /// The qualified tool name a node with declared <see cref="ProcessNode.Outcomes"/> is instructed to call to
    /// report its result. Internal, not a public wire contract yet — nothing outside this package needs to name it
    /// today; <c>Thalos.NET.Tests.Workflow</c> reaches it through <c>InternalsVisibleTo</c>.
    /// </summary>
    internal const string OutcomeToolName = "workflow__report_outcome";

    /// <summary>
    /// The single argument name a call to <see cref="OutcomeToolName"/> is read from. Aliases
    /// <see cref="OutcomeToolSchema.ArgumentName"/> rather than repeating the literal: the side that offers the tool
    /// spells the argument from that constant, and a second literal here could drift from it silently - a
    /// well-formed call whose argument this side did not recognise would read as "no outcome reported".
    /// </summary>
    internal const string OutcomeArgumentName = OutcomeToolSchema.ArgumentName;

    private static readonly IReadOnlyDictionary<string, object?> EmptyVariables =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISubagentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IWorkflowReferenceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IReadOnlyDictionary<(string Process, int Version), ProcessDefinition> _processes =
        processes ?? throw new ArgumentNullException(nameof(processes));
    private readonly Func<WorkflowRun, ISecurityContext> _resolveCaller =
        resolveCaller ?? throw new ArgumentNullException(nameof(resolveCaller));

    /// <summary>
    /// Advances the run named in <paramref name="message"/> by one node: loads it, runs its agent turn, and
    /// persists whatever <see cref="WorkflowInterpreter.Advance"/> decides. Never throws for a node-level failure —
    /// see this class's remarks — but does not catch an unexpected exception from <see cref="ISubagentRunner.RunAsync"/>
    /// or a <see cref="WorkflowConcurrencyException"/> from <see cref="IWorkflowStore"/>: both indicate the outbox
    /// should retry, not that this node's outcome was decided.
    /// </summary>
    public async ValueTask DispatchAsync(WorkflowDispatchMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var run = await _store.FindAsync(message.RunId, ct).ConfigureAwait(false);

        // The outbox is at-least-once: a message can be redelivered after the run it names has already moved on,
        // whether because this same dispatch already completed it or because a concurrent delivery won the race.
        // message.Seq travels with the message for exactly this comparison — a run not found, already past this
        // seq, or no longer Running (parked, or already terminal) all mean there is nothing left for this
        // particular message to do. Dropped silently, not failed: this is ordinary at-least-once delivery, not an
        // error.
        if (run is null || run.CurrentSeq != message.Seq || run.Status != WorkflowStatus.Running)
        {
            return;
        }

        // Seq already matches at this point, so a node-name mismatch is not an ordinary redelivery race - it
        // would mean the message and the run's own state disagree about where the run is, which is corruption
        // worth failing loudly on rather than silently acting on the wrong node.
        if (!string.Equals(message.Node, run.CurrentNode, StringComparison.Ordinal))
        {
            await _store.FailAsync(run.Id, $"dispatch message named node '{message.Node}' but run '{run.Id}' is at '{run.CurrentNode}' (seq {message.Seq} matched).", ct).ConfigureAwait(false);
            return;
        }

        var resolved = await ResolveNodeAsync(run, ct).ConfigureAwait(false);
        if (resolved is not { } target)
        {
            return;
        }

        // A terminal node and a gate both declare no agent or skill - ProcessValidator's "exactly one of task,
        // gate or terminal" rule guarantees a node with Await or Terminal set has Agent null. Reaching either
        // always takes two Advance calls: the first, on the branch/next edge that led here, reports NextStatus
        // Running (ApplyCap does not special-case either kind of target), which is exactly why a dispatch was
        // enqueued for it at all; this second call, made directly against an empty result with nothing to run,
        // is what actually yields the terminal status or parks the gate at Awaiting. A gate arriving here is
        // always a fresh arrival, never a resume - Advance parks whenever run.Status is not already Awaiting,
        // and this method only ever reaches Advance with the run's persisted Running status (the guard above)
        // - so IWorkflowStore.ResumeAsync, not this dispatcher, is what later calls Advance again with Awaiting
        // set to actually leave the gate.
        if (target.Node.Terminal is not null || target.Node.Await is not null)
        {
            await AdvanceAndPersistAsync(run, target.Process, new NodeResult(null, EmptyVariables), message.Seq, ct).ConfigureAwait(false);
            return;
        }

        var agentId = await ResolveAgentAsync(run, target.Node, ct).ConfigureAwait(false);
        if (agentId is not { } resolvedAgentId)
        {
            return;
        }

        await RunNodeAsync(run, target.Process, target.Node, resolvedAgentId, message.Seq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves <paramref name="node"/>'s <see cref="ProcessNode.Agent"/> name to an <see cref="AgentId"/> through
    /// <see cref="IWorkflowReferenceResolver.ResolveAgentIdAsync"/> — <c>agent:</c> is a human-authored name in a
    /// git-reviewed process file, never a raw id, so this dispatcher never parses it as one itself. Fails the run
    /// (not throwing — see this class's remarks) when the node has no agent name at all, or the name does not
    /// resolve.
    /// </summary>
    private async ValueTask<AgentId?> ResolveAgentAsync(WorkflowRun run, ProcessNode node, CancellationToken ct)
    {
        if (node.Agent is not { } agentName)
        {
            await _store.FailAsync(run.Id, $"node '{run.CurrentNode}' is a task node with no agent name.", ct).ConfigureAwait(false);
            return null;
        }

        var agentId = await _resolver.ResolveAgentIdAsync(agentName, ct).ConfigureAwait(false);
        if (agentId is null)
        {
            await _store.FailAsync(run.Id, $"node '{run.CurrentNode}' references unknown agent '{agentName}'.", ct).ConfigureAwait(false);
        }

        return agentId;
    }

    /// <summary>
    /// Looks up <paramref name="run"/>'s process and current node, failing the run (not throwing — see this
    /// class's remarks) when either is missing: a run pointing at an unregistered process or a node the loaded
    /// definition no longer has is a configuration problem no outbox retry would fix.
    /// </summary>
    private async ValueTask<(ProcessDefinition Process, ProcessNode Node)?> ResolveNodeAsync(WorkflowRun run, CancellationToken ct)
    {
        if (!_processes.TryGetValue((run.Process, run.ProcessVersion), out var process))
        {
            await _store.FailAsync(run.Id, $"No process definition registered for '{run.Process}' version {run.ProcessVersion}.", ct).ConfigureAwait(false);
            return null;
        }

        if (!process.Nodes.TryGetValue(run.CurrentNode, out var node))
        {
            await _store.FailAsync(run.Id, $"Process '{run.Process}' version {run.ProcessVersion} has no node named '{run.CurrentNode}'.", ct).ConfigureAwait(false);
            return null;
        }

        return (process, node);
    }

    /// <summary>
    /// Runs <paramref name="node"/>'s agent turn and persists whatever <see cref="WorkflowInterpreter.Advance"/>
    /// decides from it. Every path that reaches a verdict — the turn itself failing, an ambiguous or missing
    /// outcome, or <c>Advance</c> rejecting the outcome it produced — goes through <see cref="IWorkflowStore.FailAsync"/>,
    /// never a throw; see this class's remarks for why.
    /// </summary>
    private async ValueTask RunNodeAsync(WorkflowRun run, ProcessDefinition process, ProcessNode node, AgentId agentId, long seq, CancellationToken ct)
    {
        var outcomeTool = node.Outcomes.Count > 0
            ? new OutcomeToolSchema(OutcomeToolName, node.Outcomes)
            : null;

        var request = new SubagentRunRequest
        {
            AgentId = agentId,
            Task = BuildTaskText(node, outcomeTool),
            Caller = _resolveCaller(run),
            RequiredOutcome = outcomeTool,
        };

        var turn = await _runner.RunAsync(request, ct).ConfigureAwait(false);
        if (turn.IsFailure)
        {
            // The turn ran and produced a verdict we dislike — a result, not an infrastructure gap. Takes the
            // same FailAsync path AdvanceAndPersistAsync uses for a rejected outcome, not a throw.
            await _store.FailAsync(run.Id, turn.Error.ToString(), ct).ConfigureAwait(false);
            return;
        }

        var nodeResult = BuildNodeResult(run.CurrentNode, turn.Value, outcomeTool);
        if (nodeResult.IsFailure)
        {
            await _store.FailAsync(run.Id, nodeResult.Error, ct).ConfigureAwait(false);
            return;
        }

        await AdvanceAndPersistAsync(run, process, nodeResult.Value, seq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared tail of every path through <see cref="DispatchAsync"/> that has a <see cref="NodeResult"/> in
    /// hand — a completed agent turn, or the empty result a terminal node or gate is advanced with. Calls
    /// <see cref="WorkflowInterpreter.Advance"/> and either fails the run (a result Advance rejects, e.g. an
    /// outcome outside the node's declared set — see this class's remarks on why that is <see cref="IWorkflowStore.FailAsync"/>
    /// and never a throw) or persists the transition. A <see cref="WorkflowConcurrencyException"/> from
    /// <see cref="IWorkflowStore.CompleteNodeAsync"/> is deliberately not caught here: it means another delivery
    /// of this same message (or a resume) already completed this node first, which is exactly the shape of
    /// failure a retry resolves — the next redelivery reads a run whose <see cref="WorkflowRun.CurrentSeq"/> has
    /// moved on and is dropped by <see cref="DispatchAsync"/>'s own guard.
    /// </summary>
    private async ValueTask AdvanceAndPersistAsync(WorkflowRun run, ProcessDefinition process, NodeResult nodeResult, long seq, CancellationToken ct)
    {
        var transition = WorkflowInterpreter.Advance(process, run, nodeResult);
        if (transition.IsFailure)
        {
            // This is the case the structural constraint exists to make survivable rather than silently wrong:
            // the model reported something outside the node's declared outcomes (or violated some other rule
            // Advance checks). Advance already refused to guess a branch; this dispatcher's only job is to record
            // that refusal as a failed run, naming the offending value and node via Advance's own error message,
            // rather than letting a bad outcome silently pick one.
            await _store.FailAsync(run.Id, transition.Error, ct).ConfigureAwait(false);
            return;
        }

        await _store.CompleteNodeAsync(run.Id, seq, transition.Value, nodeResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the node result Advance evaluates. For a node with no declared outcomes, the outcome is always
    /// <see langword="null"/> — nothing to constrain. For a node with declared outcomes, the value comes only
    /// from a call to <paramref name="outcomeTool"/>'s <see cref="OutcomeToolSchema.ToolName"/> in
    /// <see cref="AgentTurnResult.ToolCalls"/>: never from <see cref="AgentTurnResult.Text"/>. A model that never
    /// called the tool fails the node right here, with a message that names <paramref name="nodeName"/> and says
    /// plainly that no outcome was reported — distinct from the message <see cref="WorkflowInterpreter.Advance"/>
    /// produces for a call that reported a value outside the declared set, so the event log can tell a silent
    /// model apart from a miscategorising one. This method does not otherwise pre-validate the reported value
    /// against <see cref="OutcomeToolSchema.AllowedValues"/> itself, because <c>Advance</c> is the single place
    /// that check already lives and duplicating it here would only create a second place for the two to drift.
    /// </summary>
    private static Result<NodeResult> BuildNodeResult(string nodeName, AgentTurnResult turn, OutcomeToolSchema? outcomeTool)
    {
        if (outcomeTool is null)
        {
            return Result<NodeResult>.Success(new NodeResult(null, EmptyVariables));
        }

        var extraction = ExtractOutcome(turn, outcomeTool.ToolName);
        if (extraction.IsFailure)
        {
            return Result<NodeResult>.Failure($"node '{nodeName}' {extraction.Error}");
        }

        if (extraction.Value is null)
        {
            return Result<NodeResult>.Failure($"node '{nodeName}' completed without calling '{outcomeTool.ToolName}' to report an outcome.");
        }

        return Result<NodeResult>.Success(new NodeResult(extraction.Value, EmptyVariables));
    }

    /// <summary>
    /// Reads the outcome strictly from tool calls named <paramref name="toolName"/> — never from
    /// <see cref="AgentTurnResult.Text"/>. This is the read-side half of the structural constraint: even a model
    /// that ignored the tool's schema and free-texted its answer into <c>Text</c> gets no consideration here, the
    /// same as a model that never called the tool at all. Malformed <see cref="ToolCallSummary.ArgumentsJson"/> —
    /// not valid JSON, or missing/non-string <see cref="OutcomeArgumentName"/> — is treated as though that
    /// particular call did not report a value, rather than thrown: it is still a turn that completed, just one
    /// call this dispatcher cannot make sense of. Two or more matching calls that disagree on the value fail
    /// outright — picking one would be exactly the guess this task exists to eliminate — while repeated calls
    /// that agree, or a single call, resolve to that one value. A <see langword="null"/> success value means no
    /// call reported anything usable at all, which <see cref="BuildNodeResult"/> turns into its own explicit
    /// "no outcome reported" failure rather than letting a silent <see langword="null"/> reach <c>Advance</c>.
    /// </summary>
    private static Result<string?> ExtractOutcome(AgentTurnResult turn, string toolName)
    {
        string? outcome = null;
        foreach (var call in turn.ToolCalls)
        {
            if (!string.Equals(call.ToolName, toolName, StringComparison.Ordinal))
            {
                continue;
            }

            var value = TryReadOutcomeArgument(call.ArgumentsJson);
            if (value is null)
            {
                continue;
            }

            if (outcome is not null && !string.Equals(outcome, value, StringComparison.Ordinal))
            {
                return Result<string?>.Failure(
                    $"called '{toolName}' more than once with disagreeing values ('{outcome}' and '{value}') — refusing to guess which one to use");
            }

            outcome = value;
        }

        return Result<string?>.Success(outcome);
    }

    /// <summary>Reads the <see cref="OutcomeArgumentName"/> string argument out of one tool call's JSON, or <see langword="null"/> if it isn't there or isn't a string.</summary>
    private static string? TryReadOutcomeArgument(string argumentsJson)
    {
        try
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return arguments.RootElement.TryGetProperty(OutcomeArgumentName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the single user-message instruction sent as <see cref="SubagentRunRequest.Task"/>. <see cref="ProcessNode"/>
    /// carries no free-text unit-of-work description today — Tasks 1 through 4 never added one — so this is built
    /// purely from the node's skill pin and, when present, its outcome contract. Wiring in the actual work item
    /// (an issue, a diff, prior turns' variables) is a host concern layered on top, exactly as
    /// <see cref="SubagentRunRequest.Caller"/> is; see this class's remarks.
    /// </summary>
    private static string BuildTaskText(ProcessNode node, OutcomeToolSchema? outcomeTool)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(node.Skill))
        {
            lines.Add($"Use skill '{node.Skill}' to complete this task.");
        }

        if (outcomeTool is not null)
        {
            lines.Add($"When finished, report your result by calling the '{outcomeTool.ToolName}' tool with '{OutcomeArgumentName}' set to exactly one of: {string.Join(", ", outcomeTool.AllowedValues)}.");
        }

        return string.Join('\n', lines);
    }
}
