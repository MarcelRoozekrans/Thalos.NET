using Microsoft.Agents.AI;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

public interface IAgentFactory
{
    ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct);
    void Invalidate(AgentId agentId);
}
