using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>Aggregates all <see cref="IToolSource"/>s, qualifies names, filters by the agent's allow-list, wraps for authorization.</summary>
public sealed partial class ToolCatalog(
    IEnumerable<IToolSource> sources,
    IToolAuthorizer authorizer,
    IAgentNotificationPublisher publisher,
    TimeProvider clock,
    ILogger<ToolCatalog>? logger = null) : IToolCatalog
{
    private readonly IReadOnlyList<IToolSource> _sources = sources.ToList();
    private readonly ILogger<ToolCatalog> _logger = logger ?? NullLogger<ToolCatalog>.Instance;

    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> ResolveAsync(AgentDefinition agent, CancellationToken ct)
    {
        var result = new List<AITool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in _sources)
        {
            var tools = await source.GetToolsAsync(ct).ConfigureAwait(false);
            if (tools.IsFailure)
            {
                LogSourceFailed(_logger, source.Name, tools.Error.ToString());
                continue;
            }

            foreach (var tool in tools.Value)
            {
                if (tool is not AIFunction fn)
                {
                    continue; // only functions are callable
                }

                var qualified = $"{source.Name}__{fn.Name}";
                if (!IsAllowed(agent.Tools, qualified))
                {
                    continue;
                }

                if (!seen.Add(qualified))
                {
                    LogDuplicateTool(_logger, qualified);
                    continue;
                }

                result.Add(new AuthorizingAIFunction(fn, qualified, authorizer, publisher, clock));
            }
        }

        LogResolved(_logger, agent.Name, result.Count);
        return Result<IReadOnlyList<AITool>, AgentError>.Success(result);
    }

    // Plain loop instead of LINQ Any(): the analyzer (ZA0601) flags the per-iteration closure allocation inside the tool loop.
    private static bool IsAllowed(IReadOnlyList<string> patterns, string qualifiedToolName)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (Glob.IsMatch(patterns[i], qualifiedToolName))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "Tool source '{Source}' failed and was skipped: {Error}")]
    private static partial void LogSourceFailed(ILogger logger, string source, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Duplicate tool '{Tool}' ignored (first registration wins)")]
    private static partial void LogDuplicateTool(ILogger logger, string tool);

    [LoggerMessage(EventId = 102, Level = LogLevel.Debug, Message = "Resolved {Count} tools for agent '{Agent}'")]
    private static partial void LogResolved(ILogger logger, string agent, int count);
}
