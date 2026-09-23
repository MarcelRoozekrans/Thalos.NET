using System.Text.Json;
using Microsoft.Extensions.Logging;
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
/// The run's <see cref="ProcessDefinition"/> arrives the same way, through <see cref="IProcessDefinitionStore"/>,
/// resolved per dispatch on the run's pinned <c>(Process, ProcessVersion)</c> — the store is the single source of
/// truth for what a process is, so a definition that was synced is runnable and one that was not is not, with no
/// separately populated registry able to hold a different answer.
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
public sealed partial class WorkflowNodeDispatcher(
    IWorkflowStore store,
    ISubagentRunner runner,
    IWorkflowReferenceResolver resolver,
    IProcessDefinitionStore definitions,
    Func<WorkflowRun, ISecurityContext> resolveCaller,
    ILogger<WorkflowNodeDispatcher>? logger = null)
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

    /// <summary>
    /// The optional second argument name a call to <see cref="OutcomeToolName"/> may carry: a JSON object of
    /// variables to merge into the run. Aliases <see cref="OutcomeToolSchema.VariablesArgumentName"/> for the
    /// same reason <see cref="OutcomeArgumentName"/> aliases its counterpart - the offering side spells it from
    /// that constant, and a second literal here could drift from it silently.
    /// </summary>
    internal const string VariablesArgumentName = OutcomeToolSchema.VariablesArgumentName;

    private static readonly IReadOnlyDictionary<string, object?> EmptyVariables =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISubagentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IWorkflowReferenceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IProcessDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    private readonly Func<WorkflowRun, ISecurityContext> _resolveCaller =
        resolveCaller ?? throw new ArgumentNullException(nameof(resolveCaller));

    /// <summary>
    /// Where this dispatcher tells a host operator what the reading agent can already see. Optional and last so
    /// every existing five-argument construction still compiles, and defaulted to a no-op rather than made
    /// required: a host that wires no logger loses the operator's view of dropped variables, not the dispatch.
    /// </summary>
    private readonly ILogger _logger = logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

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
    /// Resolves <paramref name="run"/>'s process definition through <see cref="IProcessDefinitionStore.GetAsync"/>
    /// and finds its current node, failing the run (not throwing — see this class's remarks) when either does not
    /// resolve. The definition comes from the store on the run's pinned <c>(Process, ProcessVersion)</c>, never
    /// from a dictionary handed in at composition time: the store is the one place a definition lives, so a
    /// version that was synced is runnable and a version that was not is not, with no second registration step
    /// able to disagree with the table.
    /// </summary>
    /// <remarks>
    /// Both failures here are node failures, routed through <see cref="IWorkflowStore.FailAsync"/> rather than
    /// thrown, for the reason this class's remarks give: an unresolvable definition is a configuration problem
    /// that eight outbox retries would only repeat, each one re-running a paid agent turn. The store's own error
    /// message already names the process and version, so it is passed through verbatim rather than re-worded into
    /// something that could drift from it. An <em>exception</em> escaping <c>GetAsync</c> is a different thing —
    /// the backing store being unreachable — and is deliberately left to propagate, because that is transient and
    /// a retry genuinely can fix it.
    /// </remarks>
    private async ValueTask<(ProcessDefinition Process, ProcessNode Node)?> ResolveNodeAsync(WorkflowRun run, CancellationToken ct)
    {
        var definition = await _definitions.GetAsync(run.Process, run.ProcessVersion, ct).ConfigureAwait(false);
        if (definition.IsFailure)
        {
            await _store.FailAsync(run.Id, definition.Error, ct).ConfigureAwait(false);
            return null;
        }

        var process = definition.Value;
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
            Task = BuildTaskText(run, node, outcomeTool),
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

        var nodeResult = BuildNodeResult(run, turn.Value, outcomeTool);
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
    /// <see langword="null"/> and the variable bag is always empty — there is no outcome tool for such a node, so
    /// it has no read path to report either through, which is correct rather than a gap: a node that declares
    /// nothing produces nothing. For a node with declared outcomes, both the value and the variables come only
    /// from a call to <paramref name="outcomeTool"/>'s <see cref="OutcomeToolSchema.ToolName"/> in
    /// <see cref="AgentTurnResult.ToolCalls"/>: never from <see cref="AgentTurnResult.Text"/>. A model that never
    /// called the tool fails the node right here, with a message that names <paramref name="run"/>'s current node and says
    /// plainly that no outcome was reported — distinct from the message <see cref="WorkflowInterpreter.Advance"/>
    /// produces for a call that reported a value outside the declared set, so the event log can tell a silent
    /// model apart from a miscategorising one. This method does not otherwise pre-validate the reported value
    /// against <see cref="OutcomeToolSchema.AllowedValues"/> itself, because <c>Advance</c> is the single place
    /// that check already lives and duplicating it here would only create a second place for the two to drift.
    /// <para>
    /// <paramref name="run"/> rather than just its node name, because the two key-space caps are checked here and
    /// the bag cap needs the keys the run already holds: overwriting one of those is always allowed, minting a
    /// new one past the limit is not. See <see cref="CheckKeyLimits"/>.
    /// </para>
    /// </summary>
    private static Result<NodeResult> BuildNodeResult(WorkflowRun run, AgentTurnResult turn, OutcomeToolSchema? outcomeTool)
    {
        var nodeName = run.CurrentNode;
        if (outcomeTool is null)
        {
            return Result<NodeResult>.Success(new NodeResult(null, EmptyVariables));
        }

        var extraction = ExtractReport(turn, outcomeTool.ToolName);
        if (extraction.IsFailure)
        {
            return Result<NodeResult>.Failure($"node '{nodeName}' {extraction.Error}");
        }

        var report = extraction.Value;
        if (report.Outcome is null)
        {
            return Result<NodeResult>.Failure($"node '{nodeName}' completed without calling '{outcomeTool.ToolName}' to report an outcome.");
        }

        if (report.Variables is { Count: > 0 } reported && CheckKeyLimits(nodeName, run.Variables, reported) is { } limitError)
        {
            return Result<NodeResult>.Failure(limitError);
        }

        return Result<NodeResult>.Success(new NodeResult(report.Outcome, report.Variables ?? EmptyVariables));
    }

    /// <summary>
    /// Enforces the two caps on the variable key space, returning the failure message for a report that breaches
    /// either, or <see langword="null"/> when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the key space is bounded at all, and why rejecting is the right answer.</b> The omission notice can
    /// only name every key it left out if the number of keys that can exist is itself bounded — otherwise an
    /// agent mints enough names to push a real one out of any fixed-size list, and widening the list only moves
    /// the threshold. So the bound lives here, on what may be written, rather than on what is displayed.
    /// </para>
    /// <para>
    /// A breach fails the node rather than trimming the report, because a rejected report is a contract a process
    /// author can read back out of <see cref="WorkflowRun.LastError"/> and the event log, while a silently
    /// trimmed one looks exactly like a node that chose to report less. Both messages name the cap and the
    /// numbers so the author can see which one was hit.
    /// </para>
    /// <para>
    /// The per-report cap counts the whole turn's contribution, already merged across every matching call, so it
    /// cannot be sidestepped by splitting one report over several calls. The bag cap counts <em>distinct</em>
    /// keys after the merge: overwriting a key that already exists never moves that count, which is what lets a
    /// capped loop overwrite the same few keys lap after lap without ever hitting this.
    /// </para>
    /// </remarks>
    private static string? CheckKeyLimits(string nodeName, IReadOnlyDictionary<string, object?> existing, IReadOnlyDictionary<string, object?> reported)
    {
        if (reported.Count > WorkflowVariableBlock.MaxVariablesPerReport)
        {
            return $"node '{nodeName}' reported {reported.Count} variables in one turn, more than the {WorkflowVariableBlock.MaxVariablesPerReport} a single node may contribute. Report fewer, larger-grained variables.";
        }

        var distinct = existing.Count;
        foreach (var key in reported.Keys)
        {
            if (!existing.ContainsKey(key))
            {
                distinct++;
            }
        }

        return distinct > WorkflowVariableBlock.MaxVariableKeys
            ? $"node '{nodeName}' would take the run's variable bag to {distinct} distinct keys, past the limit of {WorkflowVariableBlock.MaxVariableKeys}. Overwriting a key the run already holds is always allowed; minting a new one past the limit is not."
            : null;
    }

    /// <summary>
    /// Reads the outcome, and any variables reported alongside it, strictly from tool calls named
    /// <paramref name="toolName"/> — never from <see cref="AgentTurnResult.Text"/>. This is the read-side half of
    /// the structural constraint: even a model that ignored the tool's schema and free-texted its answer into
    /// <c>Text</c> gets no consideration here, the same as a model that never called the tool at all. Variables
    /// travel that same single read path deliberately, rather than a second tool: two paths would be two places
    /// to keep in step, and a free-texted variable gets exactly as much consideration as a free-texted outcome,
    /// which is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Malformed <see cref="ToolCallSummary.ArgumentsJson"/> is treated as though that particular call did not
    /// report a value, rather than thrown: it is still a turn that completed, just one call this dispatcher
    /// cannot make sense of. Malformed means any of — not valid JSON; a root that is not a JSON object; a missing
    /// or non-string <see cref="OutcomeArgumentName"/>; or a <see cref="VariablesArgumentName"/> that is present
    /// and is neither a JSON object nor JSON <c>null</c>. That last one discards the call's outcome as well, and
    /// that is the point: the alternative is accepting the outcome while quietly dropping what the node reported
    /// alongside it, which is a silent discard of a node's own output. A JSON <c>null</c> is <em>not</em>
    /// malformed — the argument is optional, and an explicit null is an ordinary way to spell an omitted optional
    /// argument, so it reads as "no variables" exactly like leaving it out.
    /// </para>
    /// <para>
    /// Two or more matching calls that disagree on the outcome fail outright — picking one would be exactly the
    /// guess this task exists to eliminate — while repeated calls that agree, or a single call, resolve to that
    /// one value. Because disagreement fails before anything is merged, variables can only ever accumulate across
    /// calls that agreed on the outcome; within those, later writes win on key collision, the same rule
    /// <see cref="WorkflowRun.Variables"/> documents for the run-level bag. That is not the same kind of guess an
    /// outcome would be: a bag accumulates by definition, a branch decision does not.
    /// </para>
    /// <para>
    /// A <see langword="null"/> outcome on success means no call reported anything usable at all, which
    /// <see cref="BuildNodeResult"/> turns into its own explicit "no outcome reported" failure rather than letting
    /// a silent <see langword="null"/> reach <c>Advance</c>.
    /// </para>
    /// </remarks>
    private static Result<ReportedResult> ExtractReport(AgentTurnResult turn, string toolName)
    {
        string? outcome = null;
        Dictionary<string, object?>? variables = null;

        foreach (var call in turn.ToolCalls)
        {
            if (!string.Equals(call.ToolName, toolName, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryReadReportedCall(call.ArgumentsJson) is not { } reported)
            {
                continue;
            }

            if (outcome is not null && !string.Equals(outcome, reported.Outcome, StringComparison.Ordinal))
            {
                return Result<ReportedResult>.Failure(
                    $"called '{toolName}' more than once with disagreeing values ('{outcome}' and '{reported.Outcome}') — refusing to guess which one to use");
            }

            outcome = reported.Outcome;

            if (reported.Variables is { Count: > 0 } reportedVariables)
            {
                variables ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, value) in reportedVariables)
                {
                    variables[key] = value;
                }
            }
        }

        return Result<ReportedResult>.Success(new ReportedResult(outcome, variables));
    }

    /// <summary>
    /// Reads one tool call's arguments into the outcome it reported and the variables it carried, or
    /// <see langword="null"/> when this call reported nothing usable — see <see cref="ExtractReport"/>'s remarks
    /// for exactly which shapes that covers and why.
    /// </summary>
    /// <remarks>
    /// The root's <see cref="JsonValueKind"/> is checked before any property is read because
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws
    /// <see cref="InvalidOperationException"/> — not <see cref="JsonException"/> — on a root that is not an
    /// object, so the <c>catch</c> below would not contain it and the throw would escape the dispatcher into an
    /// outbox retry instead of failing the node.
    /// </remarks>
    private static ReportedCall? TryReadReportedCall(string argumentsJson)
    {
        try
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            var root = arguments.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(OutcomeArgumentName, out var outcome)
                || outcome.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (!root.TryGetProperty(VariablesArgumentName, out var variables) || variables.ValueKind == JsonValueKind.Null)
            {
                return new ReportedCall(outcome.GetString()!, null);
            }

            return variables.ValueKind == JsonValueKind.Object
                ? new ReportedCall(outcome.GetString()!, WorkflowVariableBlock.ReadObject(variables))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What one call to the outcome tool reported: its outcome, and the variables it carried, if any.</summary>
    private sealed record ReportedCall(string Outcome, IReadOnlyDictionary<string, object?>? Variables);

    /// <summary>
    /// What every matching call in one turn reported, resolved: the single agreed outcome — <see langword="null"/>
    /// when no call reported a usable one — and the merged variables, <see langword="null"/> when no call carried
    /// any. <see langword="null"/> rather than an empty dictionary so "carried nothing" stays distinguishable from
    /// "carried an empty object"; both merge nothing, but only one of them is a claim the node made.
    /// </summary>
    private sealed record ReportedResult(string? Outcome, IReadOnlyDictionary<string, object?>? Variables);

    /// <summary>
    /// Builds the single user-message instruction sent as <see cref="SubagentRunRequest.Task"/>: the node's skill
    /// pin, the run's accumulated <see cref="WorkflowRun.Variables"/>, and — for a node with declared outcomes —
    /// its outcome contract, in that order. <see cref="ProcessNode"/> still carries no free-text unit-of-work
    /// description of its own; the work item reaches a run through <see cref="IWorkflowStore.StartAsync"/>'s
    /// initial variables and through what each node reports, and it is those variables that this method renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The variables are framed as untrusted, and that framing is load-bearing.</b> A variable is written by
    /// one agent and read here into another agent's prompt, so without the framing an implementer could write
    /// instructions into a variable and steer the reviewer that reads it back — which would defeat the
    /// independence a two-agent pipeline is built for. <see cref="WorkflowVariableBlock"/> renders the delimited
    /// block, escapes any attempt by a value or a key to close it, and bounds its size; see that type for what
    /// happens when the bag outgrows the block.
    /// </para>
    /// <para>
    /// A run whose bag is empty gets no block at all rather than an empty one, and a node with no declared
    /// outcomes is not told about the variables argument, because it is offered no outcome tool to pass one to.
    /// </para>
    /// </remarks>
    private string BuildTaskText(WorkflowRun run, ProcessNode node, OutcomeToolSchema? outcomeTool)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(node.Skill))
        {
            lines.Add($"Use skill '{node.Skill}' to complete this task.");
        }

        var rendered = WorkflowVariableBlock.Render(run.Variables);
        if (rendered.Block is { } block)
        {
            lines.Add(block);
        }

        // The notice inside the block tells the reading agent that entries were dropped; nothing tells the host
        // operator, and a node quietly starved of the variable naming its work item is exactly the failure that
        // has to be visible from outside the conversation.
        //
        // OmittedKeyList, never the keys themselves. It is the same already-escaped, already-bounded string the
        // block's own notice carries, built once inside Render. Joining the raw keys here instead gave the same
        // data two sinks under two sets of rules: a key containing CRLF forged an extra line inside this very
        // warning, and an unbounded key count made the record hundreds of kilobytes. There is now no raw form to
        // reach for. The IsEnabled guard is belt and braces — the string is already built either way — so that a
        // future argument here cannot become work a disabled logger still pays for.
        if (rendered.OmittedCount > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            LogVariablesOmitted(
                _logger, run.Id, run.CurrentNode, rendered.OmittedCount, run.Variables.Count, rendered.OmittedKeyList);
        }

        if (outcomeTool is not null)
        {
            lines.Add($"When finished, report your result by calling the '{outcomeTool.ToolName}' tool with '{OutcomeArgumentName}' set to exactly one of: {string.Join(", ", outcomeTool.AllowedValues)}.");
            lines.Add($"On that same call you may also pass '{VariablesArgumentName}': a JSON object of values later steps of this workflow should be able to read. It is the only way to hand anything on; nothing else you write is carried forward.");
        }

        return string.Join('\n', lines);
    }

    [LoggerMessage(
        EventId = 900,
        Level = LogLevel.Warning,
        Message = "Workflow run {RunId} at node '{Node}': {Omitted} of {Total} variables were left out of the task text because the rendered block would have exceeded its size ceiling. Omitted keys: {Keys}")]
    private static partial void LogVariablesOmitted(ILogger logger, Guid runId, string node, int omitted, int total, string keys);
}
