namespace Thalos;

/// <summary>Registered agent definitions.</summary>
public interface IAgentCatalog
{
    IReadOnlyList<AgentDefinition> Agents { get; }
    bool TryGet(AgentId id, out AgentDefinition definition);
}
