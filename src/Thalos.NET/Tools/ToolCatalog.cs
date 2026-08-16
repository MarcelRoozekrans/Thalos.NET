using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>Aggregates all <see cref="IToolSource"/>s, qualifies names, filters by the agent's allow-list, wraps for authorization.</summary>
/// <remarks>
/// A source whose <see cref="IToolSource.Name"/> fails <see cref="ToolSourceName.IsValid"/> (<c>^[a-zA-Z0-9_-]+$</c>, no <c>__</c>) is skipped, as is a
/// source whose <see cref="IToolSource.GetToolsAsync"/> fails; non-<see cref="AIFunction"/> tools, duplicate qualified names
/// (first wins) and qualified names longer than 64 characters (provider limit) are dropped. All of these are logged, none is fatal.
/// </remarks>
[Singleton(As = typeof(IToolCatalog))] // interface only (the default would also register the concrete type as a second instance)
public sealed partial class ToolCatalog : IToolCatalog
{
    private const int MaxQualifiedNameLength = 64;

    private readonly IReadOnlyList<IToolSource> _sources;
    private readonly IToolAuthorizer _authorizer;
    private readonly IAgentNotificationPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<ToolCatalog> _logger;
    private readonly ILogger<AuthorizingAIFunction> _functionLogger;

    /// <summary>Creates a catalog over <paramref name="sources"/>; every resolved tool is wrapped in an <see cref="AuthorizingAIFunction"/> using the given collaborators.</summary>
    public ToolCatalog(
        IEnumerable<IToolSource> sources,
        IToolAuthorizer authorizer,
        IAgentNotificationPublisher publisher,
        TimeProvider clock,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
        _authorizer = authorizer;
        _publisher = publisher;
        _clock = clock;
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<ToolCatalog>();
        _functionLogger = factory.CreateLogger<AuthorizingAIFunction>();
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> ResolveAsync(AgentDefinition agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var result = new List<AITool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in _sources)
        {
            if (!ToolSourceName.IsValid(source.Name))
            {
                LogInvalidSourceName(_logger, source.Name);
                continue;
            }

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
                    LogNonFunctionTool(_logger, source.Name, tool.Name, tool.GetType().Name);
                    continue; // only functions are callable
                }

                var qualified = $"{source.Name}__{fn.Name}";
                if (qualified.Length > MaxQualifiedNameLength)
                {
                    LogNameTooLong(_logger, qualified, MaxQualifiedNameLength);
                    continue;
                }

                if (!IsAllowed(agent.Tools, qualified))
                {
                    continue;
                }

                if (!seen.Add(qualified))
                {
                    LogDuplicateTool(_logger, qualified);
                    continue;
                }

                result.Add(new AuthorizingAIFunction(fn, qualified, _authorizer, _publisher, _clock, _functionLogger));
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

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Tool '{Tool}' from source '{Source}' is a {ToolType}, not an AIFunction, and was dropped")]
    private static partial void LogNonFunctionTool(ILogger logger, string source, string tool, string toolType);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Tool source name '{Source}' is invalid (must match ^[a-zA-Z0-9_-]+$ and not contain '__'); source skipped")]
    private static partial void LogInvalidSourceName(ILogger logger, string? source);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "Qualified tool name '{Tool}' exceeds {Max} characters and was skipped")]
    private static partial void LogNameTooLong(ILogger logger, string tool, int max);
}
