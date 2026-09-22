using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Parses a <see cref="ProcessDefinition"/> from YAML.
/// </summary>
/// <remarks>
/// Loading makes two passes over the same text, because each pass answers a question the other cannot:
/// <list type="bullet">
/// <item>
/// A pass over YamlDotNet's representation model
/// (<see cref="YamlStream"/>/<see cref="YamlMappingNode"/>) reads the document order of the "nodes" mapping's
/// keys. <see cref="YamlMappingNode.Children"/> is a <c>YamlDotNet.Helpers.OrderedDictionary&lt;,&gt;</c> that
/// preserves the order keys were written in — the only place that order is knowable. The first key found this
/// way becomes <see cref="ProcessDefinition.StartNode"/>.
/// </item>
/// <item>
/// A pass with a typed <see cref="IDeserializer"/> populates every field, including the "nodes" mapping's
/// values — deserialized into a plain <see cref="Dictionary{TKey,TValue}"/>, whose enumeration order is a
/// runtime implementation detail rather than a documented contract, so it is never relied on for
/// <see cref="ProcessDefinition.StartNode"/>.
/// </item>
/// </list>
/// </remarks>
public static class ProcessLoader
{
    private static readonly IDeserializer TypedDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parses <paramref name="yaml"/> into a <see cref="ProcessDefinition"/>. Never throws on malformed input.</summary>
    public static Result<ProcessDefinition> Load(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Result<ProcessDefinition>.Failure("The process definition YAML is empty.");
        }

        string startNode;
        try
        {
            startNode = ReadStartNode(yaml);
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or InvalidCastException)
        {
            return Result<ProcessDefinition>.Failure($"Failed to parse the process definition: {ex.Message}");
        }

        ProcessDefinitionDto? dto;
        try
        {
            dto = TypedDeserializer.Deserialize<ProcessDefinitionDto>(yaml);
        }
        catch (YamlException ex)
        {
            return Result<ProcessDefinition>.Failure($"Failed to parse the process definition: {ex.Message}");
        }

        if (dto is null)
        {
            return Result<ProcessDefinition>.Failure("The process definition YAML is empty.");
        }

        if (string.IsNullOrWhiteSpace(dto.Process))
        {
            return Result<ProcessDefinition>.Failure("The process definition has no 'process' name.");
        }

        if (dto.Nodes.Count == 0)
        {
            return Result<ProcessDefinition>.Failure("The process definition has no 'nodes' mapping.");
        }

        var nodes = new Dictionary<string, ProcessNode>(dto.Nodes.Count, StringComparer.Ordinal);
        foreach (var (name, node) in dto.Nodes)
        {
            nodes[name] = node.ToProcessNode();
        }

        return Result<ProcessDefinition>.Success(new ProcessDefinition
        {
            Name = dto.Process,
            Version = dto.Version,
            Nodes = nodes,
            StartNode = startNode,
        });
    }

    private static string ReadStartNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("The process definition YAML has no top-level mapping.");
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("nodes"), out var nodesNode) ||
            nodesNode is not YamlMappingNode nodesMapping ||
            nodesMapping.Children.Count == 0)
        {
            throw new InvalidOperationException("The process definition has no 'nodes' mapping.");
        }

        var firstKey = nodesMapping.Children.Keys.First();
        if (firstKey is not YamlScalarNode { Value: { } startNode })
        {
            throw new InvalidOperationException("The first entry in 'nodes' does not have a scalar key.");
        }

        return startNode;
    }

    private sealed class ProcessDefinitionDto
    {
        public string Process { get; set; } = "";
        public int Version { get; set; }
        public Dictionary<string, ProcessNodeDto> Nodes { get; set; } = [];
    }

    private sealed class ProcessNodeDto
    {
        public string? Agent { get; set; }
        public string? Skill { get; set; }
        public string? Next { get; set; }
        public Dictionary<string, string>? Branch { get; set; }
        public List<string>? Outcomes { get; set; }
        public string? Await { get; set; }
        public string? Terminal { get; set; }
        public int? MaxVisits { get; set; }
        public string? OnExceeded { get; set; }
        public List<string>? Models { get; set; }
        public List<string>? Lenses { get; set; }
        public int? Quorum { get; set; }

        public ProcessNode ToProcessNode() => new()
        {
            Agent = Agent,
            Skill = Skill,
            Next = Next,
            Branch = Branch ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Outcomes = Outcomes ?? [],
            Await = Await,
            Terminal = Terminal,
            MaxVisits = MaxVisits,
            OnExceeded = OnExceeded,
            Models = Models ?? [],
            Lenses = Lenses ?? [],
            Quorum = Quorum,
        };
    }
}
