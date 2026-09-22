using System.Globalization;
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
/// <see cref="SubagentRunRequest.Caller"/>.
/// </summary>
/// <remarks>
/// <b>A node failure must not throw.</b> A throw would hand the message back to the outbox for eight retries with
/// exponential backoff, re-running the subagent each time — paying for the same failing turn eight times over,
/// with nobody told until it dead-letters. A result this dispatcher dislikes — the turn itself failed, or
/// <see cref="WorkflowInterpreter.Advance"/> rejects the outcome it produced — is recorded through
/// <see cref="IWorkflowStore.FailAsync"/> instead, which marks the run <see cref="WorkflowStatus.Failed"/> without
/// leaving the outbox anything to retry. Only a turn that never produced a result at all — an unexpected exception
/// escaping <see cref="ISubagentRunner.RunAsync"/> itself, or <see cref="IWorkflowStore"/> throwing
/// <see cref="WorkflowConcurrencyException"/> because another dispatch already won the race to complete this node —
/// is allowed to propagate, because those genuinely are the transient, infrastructure-shaped failures a retry can
/// fix.
/// </remarks>
public sealed class WorkflowNodeDispatcher(
    IWorkflowStore store,
    ISubagentRunner runner,
    IReadOnlyDictionary<(string Process, int Version), ProcessDefinition> processes,
    Func<WorkflowRun, ISecurityContext> resolveCaller)
{
    /// <summary>
    /// The qualified tool name a node with declared <see cref="ProcessNode.Outcomes"/> is instructed to call to
    /// report its result. Internal, not a public wire contract yet — nothing outside this package needs to name it
    /// today; <c>Thalos.NET.Tests.Workflow</c> reaches it through <c>InternalsVisibleTo</c>.
    /// </summary>
    internal const string OutcomeToolName = "workflow__report_outcome";

    /// <summary>The single argument name a call to <see cref="OutcomeToolName"/> is read from.</summary>
    internal const string OutcomeArgumentName = "outcome";

    private static readonly IReadOnlyDictionary<string, object?> EmptyVariables =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISubagentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
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

        var resolved = await ResolveNodeAsync(run, ct).ConfigureAwait(false);
        if (resolved is not { } target)
        {
            return;
        }

        // A terminal node declares no agent or skill — ProcessValidator accepts it with neither, since it has
        // nothing left to do but end the run at its declared status (ProcessNode.Terminal). Reaching one always
        // takes two Advance calls: the first, on the branch/next edge that led here, reports NextStatus Running
        // (ApplyCap does not special-case a terminal target), which is exactly why a dispatch was enqueued for
        // it at all; this second call, made directly against an empty result with nothing to run, is what
        // actually yields the terminal status. Skipping the agent/skill resolution below for this case is not
        // an optimisation — a terminal node has no agent to resolve in the first place.
        if (target.Node.Terminal is not null)
        {
            await AdvanceAndPersistAsync(run, target.Process, new NodeResult(null, EmptyVariables), message.Seq, ct).ConfigureAwait(false);
            return;
        }

        if (!AgentId.TryParse(target.Node.Agent, CultureInfo.InvariantCulture, out var agentId))
        {
            await _store.FailAsync(run.Id, $"node '{run.CurrentNode}' has an invalid agent identifier '{target.Node.Agent}'.", ct).ConfigureAwait(false);
            return;
        }

        await RunNodeAsync(run, target.Process, target.Node, agentId, message.Seq, ct).ConfigureAwait(false);
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
    /// decides from it. Every path that reaches a verdict — the turn itself failing, or <c>Advance</c> rejecting
    /// the outcome it produced — goes through <see cref="IWorkflowStore.FailAsync"/>, never a throw; see this
    /// class's remarks for why.
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

        var nodeResult = BuildNodeResult(turn.Value, outcomeTool);
        await AdvanceAndPersistAsync(run, process, nodeResult, seq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared tail of every path through <see cref="DispatchAsync"/> that has a <see cref="NodeResult"/> in
    /// hand — a completed agent turn, or the empty result a terminal node is advanced with. Calls
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
    /// <see langword="null"/> — nothing to constrain. For a node with declared outcomes, the value comes only from
    /// a call to <paramref name="outcomeTool"/>'s <see cref="OutcomeToolSchema.ToolName"/> in
    /// <see cref="AgentTurnResult.ToolCalls"/>: never from <see cref="AgentTurnResult.Text"/>. A model that never
    /// called the tool, or called it with a value <see cref="ExtractOutcome"/> cannot read as a plain string,
    /// yields a <see langword="null"/> outcome, which <see cref="WorkflowInterpreter.Advance"/> then rejects for a
    /// branching node exactly as it would reject any other outcome outside the declared set — this method does not
    /// pre-validate against <see cref="OutcomeToolSchema.AllowedValues"/> itself, because Advance is the single
    /// place that check already lives and duplicating it here would only create a second place for the two to
    /// drift.
    /// </summary>
    private static NodeResult BuildNodeResult(AgentTurnResult turn, OutcomeToolSchema? outcomeTool) =>
        outcomeTool is null
            ? new NodeResult(null, EmptyVariables)
            : new NodeResult(ExtractOutcome(turn, outcomeTool.ToolName), EmptyVariables);

    /// <summary>
    /// Reads the outcome strictly from a tool call named <paramref name="toolName"/> — never from
    /// <see cref="AgentTurnResult.Text"/>. This is the read-side half of the structural constraint: even a model
    /// that ignored the tool's schema and free-texted its answer into <c>Text</c> gets no consideration here, the
    /// same as a model that never called the tool at all. Malformed <see cref="ToolCallSummary.ArgumentsJson"/> —
    /// not valid JSON, or missing/non-string <see cref="OutcomeArgumentName"/> — is treated the same as "no
    /// outcome reported" rather than thrown: it is still a turn that completed, just one whose tool call this
    /// dispatcher cannot make sense of, and <see cref="WorkflowInterpreter.Advance"/> already rejects a
    /// <see langword="null"/> outcome for any node that declares one.
    /// </summary>
    private static string? ExtractOutcome(AgentTurnResult turn, string toolName)
    {
        foreach (var call in turn.ToolCalls)
        {
            if (!string.Equals(call.ToolName, toolName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                if (arguments.RootElement.TryGetProperty(OutcomeArgumentName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            catch (JsonException)
            {
                // Malformed tool-call arguments are not this dispatcher's problem to throw over — see the
                // remarks above. Falls through to the next matching call, if any, or returns null below.
            }
        }

        return null;
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
