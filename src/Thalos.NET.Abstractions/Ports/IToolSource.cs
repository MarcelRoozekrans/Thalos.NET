using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>Supplies tools (MCP server, in-process functions, …). <see cref="Name"/> prefixes tool names: "{Name}__{tool}".</summary>
public interface IToolSource
{
    /// <summary>Source name; must be unique per runtime. Becomes the tool-name prefix <c>{Name}__</c> (letters, digits, <c>_</c> and <c>-</c> only).</summary>
    string Name { get; }

    /// <summary>Returns the tools this source currently offers (unqualified names; the runtime adds the prefix).</summary>
    ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct);
}
