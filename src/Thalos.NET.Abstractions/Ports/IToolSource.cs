using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>Supplies tools (MCP server, in-process functions, …). <see cref="Name"/> prefixes tool names: "{Name}__{tool}".</summary>
public interface IToolSource
{
    string Name { get; }
    ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct);
}
