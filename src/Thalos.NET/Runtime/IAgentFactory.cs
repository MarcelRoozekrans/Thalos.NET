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
    /// Checks a turn's requested <paramref name="model"/> and <paramref name="toolFilter"/> against <paramref name="definition"/>
    /// and returns <see cref="AgentError.Validation"/> for anything outside the agent's own configuration; it builds nothing and
    /// sends no chat request. A <paramref name="model"/> is accepted when it is in <see cref="AgentDefinition.AllowedModels"/> or
    /// equals the model the agent runs on anyway (<see cref="AgentDefinition.Model"/>, else the chat client provider's default).
    /// Every <paramref name="toolFilter"/> entry must match, with <c>Glob.IsMatch</c>, at least one of the agent's own resolved
    /// qualified tool names, so a filter can only narrow. <see langword="null"/> for either means no override and is not checked.
    /// </summary>
    ValueTask<UnitResult<AgentError>> ValidateOverridesAsync(AgentDefinition definition, string? model, IReadOnlyList<string>? toolFilter, CancellationToken ct);

    /// <summary>
    /// Drops every cached agent for <paramref name="agentId"/> — every revision, not just the unrevisioned one — and disposes
    /// their chat-client pipelines immediately; the next call for each rebuilds. Not turn-safe: turns in flight on that agent
    /// may fail — invalidate while quiescent.
    /// </summary>
    void Invalidate(AgentId agentId);
}
