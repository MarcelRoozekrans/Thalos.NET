using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>
/// Creates a <see cref="SkillContextProvider"/> per agent, unless skills are disabled host-wide or the agent's
/// <see cref="AgentDefinition.Skills"/> glob list is empty (the default) — an agent that asked for no skills pays nothing.
/// </summary>
public sealed class SkillContextProviderSource(
    SkillCatalogue catalogue,
    IOptions<SkillOptions> options,
    AgentEventHub hub,
    ILoggerFactory? loggerFactory = null) : IAgentContextProviderSource
{
    /// <inheritdoc />
    public AIContextProvider? CreateProvider(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return !options.Value.Enabled || agent.Skills.Count == 0
            ? null
            : new SkillContextProvider(catalogue, agent.Skills, hub, loggerFactory?.CreateLogger<SkillContextProvider>());
    }
}
