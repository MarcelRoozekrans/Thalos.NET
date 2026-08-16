using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>Resolves the concrete, authorized tool set for an agent.</summary>
public interface IToolCatalog
{
    ValueTask<Result<IReadOnlyList<AITool>, AgentError>> ResolveAsync(AgentDefinition agent, CancellationToken ct);
}
