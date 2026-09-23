namespace Thalos.Workflow;

/// <summary>
/// Pins one task node to the exact agent and skill <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>
/// dispatched it against — the agent's identity and revision, and the skill's name and content hash — so a run
/// started against one build of an agent or skill keeps running against that same build even after either is
/// edited or republished later. <see cref="RunManifest"/> holds one of these per task node.
/// </summary>
/// <param name="AgentName">The agent's display name at the moment the run was pinned, for human-readable audit trails.</param>
/// <param name="AgentId">The agent's identity — stable across revisions, unlike <paramref name="AgentName"/>.</param>
/// <param name="AgentRevision">
/// The agent definition's revision pinned by this run, or <see langword="null"/> when the agent carries none.
/// </param>
/// <param name="SkillName">The skill's name at the moment the run was pinned.</param>
/// <param name="SkillHash">A content hash identifying the exact skill body this run was pinned against.</param>
public sealed record NodePin(string AgentName, AgentId AgentId, string? AgentRevision, string SkillName, string SkillHash);

/// <summary>
/// The write-once pin a <see cref="WorkflowRun"/> carries from the moment <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>
/// creates it: which agent revision and skill version each task node runs, plus host-pinned text documents kept
/// at their full length. Nothing after <c>INSERT</c> ever updates a run's manifest — see
/// <see cref="WorkflowRun.Manifest"/> for what a <see langword="null"/> manifest means.
/// </summary>
public sealed record RunManifest
{
    /// <summary>The pin for each task node in the run's process graph, keyed by node name. Task nodes only — gates and terminals have no agent or skill to pin.</summary>
    public required IReadOnlyDictionary<string, NodePin> Nodes { get; init; }

    /// <summary>
    /// Host-pinned text, kept at full length and keyed by document name. This is what a manifest carries in place
    /// of <see cref="WorkflowRun.Variables"/> for content too long to survive a node's rendered instruction text:
    /// variables are cut at 512 characters when rendered into a node's task, so anything the run must hand a node
    /// in full — standing instructions, a long design doc — travels here instead, pinned once at start and never
    /// truncated.
    /// </summary>
    public IReadOnlyDictionary<string, string> Documents { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
