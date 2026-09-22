namespace Thalos.Workflow;

/// <summary>
/// A durable process graph loaded from YAML by <see cref="ProcessLoader"/>: a name, a version, its nodes keyed by
/// name, and the name of the node execution begins at.
/// </summary>
public sealed class ProcessDefinition
{
    /// <summary>The process's declared name.</summary>
    public required string Name { get; init; }

    /// <summary>The process's declared version.</summary>
    public required int Version { get; init; }

    /// <summary>Every node in the graph, keyed by node name.</summary>
    public required IReadOnlyDictionary<string, ProcessNode> Nodes { get; init; }

    /// <summary>
    /// The name of the node execution starts at: the first node in the YAML document's "nodes" mapping, in the
    /// order it was written. See <see cref="ProcessLoader"/> for how that order is captured.
    /// </summary>
    public required string StartNode { get; init; }
}
