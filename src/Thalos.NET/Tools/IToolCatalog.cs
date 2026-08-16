using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>Resolves the concrete, authorized tool set for an agent.</summary>
public interface IToolCatalog
{
    /// <summary>
    /// Returns the tools <paramref name="agent"/> may see: qualified as <c>{source}__{tool}</c>, filtered by
    /// <see cref="AgentDefinition.Tools"/> globs and wrapped so every invocation is authorized.
    /// </summary>
    ValueTask<Result<IReadOnlyList<AITool>, AgentError>> ResolveAsync(AgentDefinition agent, CancellationToken ct);
}
