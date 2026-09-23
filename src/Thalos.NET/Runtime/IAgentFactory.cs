using Microsoft.Agents.AI;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>Builds and caches the MAF <see cref="AIAgent"/> (chat-client pipeline + tools + history provider) for an <see cref="AgentDefinition"/>.</summary>
public interface IAgentFactory
{
    /// <summary>
    /// Returns the agent for <paramref name="definition"/>, building it on first use. Agents are cached per
    /// (<see cref="AgentDefinition.Id"/>, <see cref="AgentDefinition.Revision"/>) pair, so two pinned revisions of the same id
    /// get independent pipelines and neither evicts the other; concurrent first calls for the same pair share one build
    /// (single-flight), and a failed build is not cached, so the next call retries. Definitions are compared by value: a
    /// definition whose content (name, description, instructions, model, max output tokens, tool globs, memory settings,
    /// revision) differs from the cached one replaces the cached agent (the old pipeline is disposed); an equal definition —
    /// same or new instance — reuses it.
    /// </summary>
    ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct);

    /// <summary>
    /// Drops every cached agent for <paramref name="agentId"/> — every revision, not just the unrevisioned one — and disposes
    /// their chat-client pipelines immediately; the next call for each rebuilds. Not turn-safe: turns in flight on that agent
    /// may fail — invalidate while quiescent.
    /// </summary>
    void Invalidate(AgentId agentId);
}
