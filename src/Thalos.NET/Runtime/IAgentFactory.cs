using Microsoft.Agents.AI;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>Builds and caches the MAF <see cref="AIAgent"/> (chat-client pipeline + tools + history provider) for an <see cref="AgentDefinition"/>.</summary>
public interface IAgentFactory
{
    /// <summary>
    /// Returns the agent for <paramref name="definition"/>, building it on first use. Agents are cached per
    /// <see cref="AgentDefinition.Id"/>; concurrent first calls for the same id share one build (single-flight), and a
    /// failed build is not cached, so the next call retries. Passing a <em>different</em> <see cref="AgentDefinition"/>
    /// instance for an id that is already cached replaces the cached agent (the old pipeline is disposed) — callers
    /// should hand the factory a stable instance per definition version.
    /// </summary>
    ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct);

    /// <summary>Drops the cached agent for <paramref name="agentId"/> (if any) and disposes its chat-client pipeline; the next call rebuilds.</summary>
    void Invalidate(AgentId agentId);
}
