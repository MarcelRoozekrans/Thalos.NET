using Microsoft.Agents.AI;

namespace Thalos.Runtime;

/// <summary>
/// Supplies a MAF <see cref="AIContextProvider"/> per agent (memory recall, retrieval, …). Register with
/// <c>TryAddEnumerable</c>; <see cref="AgentFactory"/> asks every source once per agent build and attaches the non-null results
/// to <c>ChatClientAgentOptions.AIContextProviders</c> (cached with the agent). Providers must be lightweight and stateless per turn:
/// they live as long as the cached agent, are shared by every turn on it, and are not disposed by the factory.
/// </summary>
public interface IAgentContextProviderSource
{
    /// <summary>The provider to attach to <paramref name="agent"/>, or <see langword="null"/> when this source does not apply to it.</summary>
    AIContextProvider? CreateProvider(AgentDefinition agent);
}
