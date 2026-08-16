using Microsoft.Agents.AI;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>Builds and caches the MAF <see cref="AIAgent"/> (chat-client pipeline + tools + history provider) for an <see cref="AgentDefinition"/>.</summary>
public interface IAgentFactory
{
    /// <summary>
    /// Returns the agent for <paramref name="definition"/>, building it on first use. Agents are cached per
    /// <see cref="AgentDefinition.Id"/>; concurrent first calls for the same id share one build (single-flight), and a
    /// failed build is not cached, so the next call retries. Definitions are compared by value: a definition whose
    /// content (name, description, instructions, model, max output tokens, tool globs) differs from the cached one
    /// replaces the cached agent (the old pipeline is disposed); an equal definition — same or new instance — reuses it.
    /// </summary>
    ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct);

    /// <summary>
    /// Drops the cached agent for <paramref name="agentId"/> (if any) and disposes its chat-client pipeline immediately;
    /// the next call rebuilds. Not turn-safe: turns in flight on that agent may fail — invalidate while quiescent.
    /// </summary>
    void Invalidate(AgentId agentId);
}
