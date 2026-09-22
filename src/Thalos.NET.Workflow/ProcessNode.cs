namespace Thalos.Workflow;

/// <summary>
/// One node in a <see cref="ProcessDefinition"/>'s graph. A node's shape in the YAML determines which properties
/// are populated — an agent/skill step sets <see cref="Agent"/>, <see cref="Skill"/> and <see cref="Next"/> or
/// <see cref="Branch"/>; a loop-back guard adds <see cref="MaxVisits"/> and <see cref="OnExceeded"/>; an approval
/// gate sets <see cref="Await"/>; a terminal node sets <see cref="Terminal"/>. This type does not validate that
/// the combination is coherent — that is the loader's and, later, the engine's job.
/// </summary>
public sealed class ProcessNode
{
    /// <summary>The agent that runs at this node, or <see langword="null"/> for a gate or terminal node.</summary>
    public string? Agent { get; init; }

    /// <summary>The skill the agent runs, or <see langword="null"/> for a gate or terminal node.</summary>
    public string? Skill { get; init; }

    /// <summary>The unconditional successor node name, or <see langword="null"/> when <see cref="Branch"/> is used instead.</summary>
    public string? Next { get; init; }

    /// <summary>Outcome name to successor node name, for a node whose next step depends on how it finished.</summary>
    public IReadOnlyDictionary<string, string> Branch { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The outcome names this node can produce, in the order declared.</summary>
    public IReadOnlyList<string> Outcomes { get; init; } = [];

    /// <summary>The name of the external signal an approval-gate node waits on, or <see langword="null"/> otherwise.</summary>
    public string? Await { get; init; }

    /// <summary>The terminal status this node represents, or <see langword="null"/> for a non-terminal node.</summary>
    public string? Terminal { get; init; }

    /// <summary>The maximum number of times a loop-back may revisit this node before <see cref="OnExceeded"/> takes over.</summary>
    public int? MaxVisits { get; init; }

    /// <summary>The node to run once <see cref="MaxVisits"/> is exceeded.</summary>
    public string? OnExceeded { get; init; }

    /// <summary>
    /// <b>Reserved; not yet honoured.</b> The model identifiers a multi-model node would fan out to. The loader
    /// reads <c>models:</c> off the YAML and this property carries it, but nothing else in the engine looks at
    /// it: <see cref="ProcessValidator"/> has no rule for it and <see cref="WorkflowNodeDispatcher"/> never reads
    /// it. A node declaring <c>models: [sonnet, opus]</c> executes once, against whatever single model the
    /// resolved agent is configured with, with no warning that the fan-out was ignored.
    /// </summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>
    /// <b>Reserved; not yet honoured.</b> The lens identifiers a multi-lens node would evaluate against. Parsed
    /// and carried, read by nothing — see <see cref="Models"/> for the same caveat in full.
    /// </summary>
    public IReadOnlyList<string> Lenses { get; init; } = [];

    /// <summary>
    /// <b>Reserved; not yet honoured.</b> The minimum number of agreeing results a quorum node would require.
    /// Parsed and carried, read by nothing — see <see cref="Models"/> for the same caveat in full. Without a
    /// fan-out to take a quorum over, there is nothing for this number to constrain.
    /// </summary>
    public int? Quorum { get; init; }
}
