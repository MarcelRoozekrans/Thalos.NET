using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Sessions;
using Thalos.Tools;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>
/// Builds <c>provider → TurnBudgetChatClient → decorators (ascending Order) → ChatClientAgent</c>. MAF adds function
/// invocation outermost, so every decorator (e.g. AI.Sentinel) and the turn's token budget see each model round-trip
/// including tool results.
/// </summary>
/// <remarks>
/// <para>
/// The factory <em>owns</em> the chat-client pipelines it builds: the outermost decorated <see cref="IChatClient"/> is kept
/// with each cached agent and disposed on <see cref="Invalidate"/>, on replacement by a newer definition instance, and on
/// <see cref="Dispose"/>. Providers that want to share a transport across agents must do so internally (see
/// <see cref="IChatClientProvider.CreateChatClient"/>).
/// </para>
/// <para>
/// Builds are single-flight per (<see cref="AgentId"/>, <see cref="AgentDefinition.Revision"/>) pair: N concurrent first calls
/// for the same pair result in one <see cref="IChatClientProvider.CreateChatClient"/> call and reference-equal agents. Two
/// pinned revisions of the same id build and cache independently, so alternating between them never evicts the other. Failed
/// builds are not cached.
/// </para>
/// <para>
/// A cached agent is reused while the supplied <see cref="AgentDefinition"/> is equal by value (id, name, description,
/// instructions, model, max output tokens, tool globs, skill globs, memory settings, revision); a changed definition rebuilds
/// and disposes the old pipeline.
/// </para>
/// <para>
/// Every registered <see cref="IAgentContextProviderSource"/> is asked once per build; the non-null
/// <see cref="AIContextProvider"/>s are attached to the agent (<c>ChatClientAgentOptions.AIContextProviders</c>) and cached with it.
/// </para>
/// <para>
/// <see cref="Invalidate"/> (and replacement by a changed definition) disposes the pipeline immediately and is <em>not</em>
/// turn-safe: turns in flight on that agent may fail. Drive invalidation from configuration reload while the agent is
/// quiescent; ref-counted leases are a follow-up.
/// </para>
/// </remarks>
[Singleton(As = typeof(IAgentFactory))] // interface only: one instance owns all pipelines (the default As=all would also register the concrete type separately)
public sealed partial class AgentFactory : IAgentFactory, IDisposable
{
    private readonly IChatClientProvider _provider;
    private readonly IReadOnlyList<IChatClientDecorator> _decorators;
    private readonly IToolCatalog _toolCatalog;
    private readonly SessionStoreChatHistoryProvider _historyProvider;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<IAgentContextProviderSource> _contextProviderSources;
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<Result<Entry, AgentError>>>> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, ResolvedTools> _resolvedTools = new();
    private long _invalidations; // bumped by Invalidate, so a resolution in flight across it is not cached
    private volatile bool _disposed;

    /// <summary>
    /// Cache key: an agent pipeline is built once per (<see cref="AgentDefinition.Id"/>, <see cref="AgentDefinition.Revision"/>,
    /// tool filter) triple, so two runs holding two revisions of the same agent get two independent pipelines that don't
    /// evict each other, and a turn's tool filter gets a pipeline offering only its tools. <paramref name="ToolFilterKey"/> is
    /// the filter's distinct entries after an ordinal sort (see <see cref="ToToolFilterKey"/>), so equal sets share one
    /// pipeline; it is <see langword="null"/> without a filter. The resolved-tools cache uses the key without a filter.
    /// </summary>
    private readonly record struct CacheKey(AgentId Id, string? Revision, string? ToolFilterKey);

    /// <summary>One successful resolution of an agent's tools, kept with the definition it was resolved for.</summary>
    private sealed record ResolvedTools(AgentDefinition Definition, IReadOnlyList<AITool> Tools);

    /// <summary>Resolved by DI; <paramref name="contextProviderSources"/> are every registered <see cref="IAgentContextProviderSource"/> (Thalos.NET.Memory registers one).</summary>
    public AgentFactory(
        IChatClientProvider provider,
        IEnumerable<IChatClientDecorator> decorators,
        IToolCatalog toolCatalog,
        SessionStoreChatHistoryProvider historyProvider,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        IEnumerable<IAgentContextProviderSource>? contextProviderSources = null)
    {
        _provider = provider;
        _decorators = decorators.OrderBy(d => d.Order).ToList();
        _toolCatalog = toolCatalog;
        _historyProvider = historyProvider;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<AgentFactory>() ?? NullLogger<AgentFactory>.Instance;
        _contextProviderSources = contextProviderSources?.ToList() ?? [];
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> ValidateOverridesAsync(
        AgentDefinition definition, string? model, IReadOnlyList<string>? toolFilter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (model is not null
            && !definition.AllowedModels.Contains(model, StringComparer.Ordinal)
            && !string.Equals(model, definition.Model ?? _provider.DefaultModel, StringComparison.Ordinal))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation($"Model '{model}' is not in agent '{definition.Name}' allowed models."));
        }

        if (toolFilter is null)
        {
            return UnitResult<AgentError>.Success();
        }

        if (toolFilter.Count == 0)
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation("Tool filter must not be empty; pass null for no filter."));
        }

        foreach (var entry in toolFilter)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation("Tool filter entry must not be blank."));
            }
        }

        var resolved = await ResolveToolsAsync(definition, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return UnitResult<AgentError>.Failure(resolved.Error);
        }

        foreach (var pattern in toolFilter)
        {
            if (!MatchesAny(pattern, resolved.Value))
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation($"Tool filter entry '{pattern}' matches none of agent '{definition.Name}' tools."));
            }
        }

        return UnitResult<AgentError>.Success();
    }

    private static bool MatchesAny(string pattern, IReadOnlyList<AITool> tools)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            if (Glob.IsMatch(pattern, tools[i].Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="definition"/>'s tools through the catalog once and caches the result per
    /// (<see cref="AgentDefinition.Id"/>, <see cref="AgentDefinition.Revision"/>) for as long as the definition is equal by
    /// value, so a filtered turn's validation and its pipeline build share one resolution and validation checks the very
    /// list the pipelines offer. A failure is not cached. <see cref="Invalidate"/> drops the cached list, and a resolution
    /// still in flight when it runs is returned to its caller but not cached.
    /// </summary>
    private async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> ResolveToolsAsync(AgentDefinition definition, CancellationToken ct)
    {
        var key = new CacheKey(definition.Id, definition.Revision, ToolFilterKey: null);
        if (_resolvedTools.TryGetValue(key, out var cached) && SameDefinition(cached.Definition, definition))
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Success(cached.Tools);
        }

        var generation = Interlocked.Read(ref _invalidations);
        var resolved = await _toolCatalog.ResolveAsync(definition, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return resolved;
        }

        // store, then check: an Invalidate that ran at any point since the resolution started has bumped the counter,
        // and the entry is removed again; checking before the store would leave a window between the check and it
        var entry = new ResolvedTools(definition, resolved.Value);
        _resolvedTools[key] = entry;
        if (Interlocked.Read(ref _invalidations) != generation)
        {
            _resolvedTools.TryRemove(KeyValuePair.Create(key, entry));
        }

        return resolved;
    }

    /// <summary>
    /// The cache-key form of a normalized tool filter: each entry as <c>{length}:{entry}</c>, concatenated. The length
    /// prefix keeps the encoding unambiguous whatever characters an entry holds, so two different sets never share a key.
    /// </summary>
    private static string? ToToolFilterKey(string[]? patterns) =>
        patterns is null ? null : string.Concat(patterns.Select(p => $"{p.Length.ToString(CultureInfo.InvariantCulture)}:{p}"));

    /// <inheritdoc cref="IAgentFactory.GetOrCreateAsync(AgentDefinition, CancellationToken)" />
    public ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct) =>
        GetOrCreateAsync(definition, toolFilter: null, ct);

    /// <inheritdoc />
    public async ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, IReadOnlyList<string>? toolFilter, CancellationToken ct)
    {
        var retriedStaleFailure = false;
        // distinct and ordinally sorted, so equal sets share one pipeline; the build matches against this copy, never the
        // caller's list, so a later mutation of that list cannot change a pipeline cached under the key it produced
        var patterns = toolFilter?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var key = new CacheKey(definition.Id, definition.Revision, ToToolFilterKey(patterns));
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var lazy = _cache.GetOrAdd(key, static (_, state) =>
                new Lazy<Task<Result<Entry, AgentError>>>(() => state.self.BuildAsync(state.definition, state.patterns), LazyThreadSafetyMode.ExecutionAndPublication),
                (self: this, definition, patterns));
            var wasCompleted = lazy.IsValueCreated && lazy.Value.IsCompleted;

            var built = await AwaitBuildAsync(key, lazy, ct).ConfigureAwait(false);
            if (built.IsFailure)
            {
                _cache.TryRemove(KeyValuePair.Create(key, lazy)); // don't cache failures; next call retries
                if (wasCompleted && !retriedStaleFailure)
                {
                    retriedStaleFailure = true; // a failure left behind by an earlier caller — retry once rather than report it
                    continue;
                }

                return Result<AIAgent, AgentError>.Failure(built.Error);
            }

            var entry = built.Value;
            if (_disposed)
            {
                _cache.TryRemove(KeyValuePair.Create(key, lazy));
                entry.Dispose(_logger); // completed after Dispose() swept the cache
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            if (!_cache.TryGetValue(key, out var current) || !ReferenceEquals(current, lazy))
            {
                entry.Dispose(_logger); // invalidated while building — rebuild
                continue;
            }

            if (!SameDefinition(entry.Definition, definition))
            {
                // The definition changed for a cached key: treat as invalidated and rebuild with the new one.
                if (_cache.TryRemove(KeyValuePair.Create(key, lazy)))
                {
                    entry.Dispose(_logger);
                }

                continue;
            }

            return Result<AIAgent, AgentError>.Success(entry.Agent);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not turn-safe: the pipeline is disposed immediately, so turns in flight on that agent may fail. Drive invalidation
    /// from configuration reload while the agent is quiescent; ref-counted leases are a follow-up.
    /// </remarks>
    public void Invalidate(AgentId agentId)
    {
        Interlocked.Increment(ref _invalidations);
        foreach (var key in _resolvedTools.Keys)
        {
            if (key.Id == agentId)
            {
                _resolvedTools.TryRemove(key, out _);
            }
        }

        foreach (var key in _cache.Keys)
        {
            if (key.Id == agentId && _cache.TryRemove(key, out var lazy))
            {
                DisposeCompleted(lazy);
            }
        }
    }

    /// <summary>Disposes every cached chat-client pipeline. Subsequent <c>GetOrCreateAsync</c> calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _disposed = true;
        _resolvedTools.Clear();
        while (!_cache.IsEmpty)
        {
            foreach (var key in _cache.Keys)
            {
                if (_cache.TryRemove(key, out var lazy))
                {
                    DisposeCompleted(lazy);
                }
            }
        }
    }

    /// <summary>Waits for a shared build with the caller's own token (the build itself is not cancelled by any single caller); a build that threw is evicted so it doesn't poison the cache.</summary>
    private async Task<Result<Entry, AgentError>> AwaitBuildAsync(CacheKey key, Lazy<Task<Result<Entry, AgentError>>> lazy, CancellationToken ct)
    {
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully)
        {
            _cache.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    private static bool SameDefinition(AgentDefinition a, AgentDefinition b) =>
        a.Id == b.Id
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Description, b.Description, StringComparison.Ordinal)
        && string.Equals(a.Instructions, b.Instructions, StringComparison.Ordinal)
        && string.Equals(a.Model, b.Model, StringComparison.Ordinal)
        && a.MaxOutputTokens == b.MaxOutputTokens
        && a.Tools.SequenceEqual(b.Tools, StringComparer.Ordinal)
        && a.Skills.SequenceEqual(b.Skills, StringComparer.Ordinal)
        && Equals(a.Memory, b.Memory)
        && string.Equals(a.Revision, b.Revision, StringComparison.Ordinal);

    private async Task<Result<Entry, AgentError>> BuildAsync(AgentDefinition definition, string[]? toolFilter)
    {
        IChatClient? client = null;
        try
        {
            var resolved = await ResolveToolsAsync(definition, CancellationToken.None).ConfigureAwait(false);
            if (resolved.IsFailure)
            {
                return Result<Entry, AgentError>.Failure(resolved.Error);
            }

            // A filter narrows the agent's own resolved tools, in their resolved order; it is never resolved against the
            // host catalog, where a pattern such as "*" would also match tools the agent was never given.
            var tools = toolFilter is null ? resolved.Value : Narrow(resolved.Value, toolFilter);

            // The budget check goes directly over the provider, below every decorator, so a decorator that answers or
            // short-circuits a call can never skip it (see TurnBudgetChatClient).
            client = new TurnBudgetChatClient(_provider.CreateChatClient(definition));
            foreach (var decorator in _decorators)
            {
                client = decorator.Decorate(client, definition, _services);
            }

            var contextProviders = new List<AIContextProvider>();
            foreach (var source in _contextProviderSources)
            {
                if (source.CreateProvider(definition) is { } contextProvider)
                {
                    contextProviders.Add(contextProvider);
                }
            }

            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Id = definition.Id.ToString(),
                Name = definition.Name,
                Description = string.IsNullOrEmpty(definition.Description) ? null : definition.Description,
                ChatHistoryProvider = _historyProvider,
                AIContextProviders = contextProviders.Count == 0 ? null : contextProviders,
                ChatOptions = new ChatOptions
                {
                    Instructions = definition.Instructions,
                    ModelId = definition.Model ?? _provider.DefaultModel,
                    MaxOutputTokens = definition.MaxOutputTokens,
                    Tools = tools.Count == 0 ? null : tools.ToList(),
                },
            }, _loggerFactory, _services);

            return Result<Entry, AgentError>.Success(new Entry(definition, agent, client));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (client is not null)
            {
                DisposeClient(client, definition.Name, _logger);
            }

            // the raw message (may contain provider/config details) goes to the log; the error carries only the exception type
            LogBuildFailed(_logger, definition.Name, ex.Message, ex);
            return Result<Entry, AgentError>.Failure(AgentError.ProviderError($"Failed to build agent '{definition.Name}'.", ex.GetType().Name));
        }
    }

    private static List<AITool> Narrow(IReadOnlyList<AITool> tools, string[] toolFilter)
    {
        var kept = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (Array.Exists(toolFilter, pattern => Glob.IsMatch(pattern, tool.Name)))
            {
                kept.Add(tool);
            }
        }

        return kept;
    }

    private static void DisposeClient(IChatClient client, string agentName, ILogger logger)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPipelineDisposeFailed(logger, agentName, ex.Message, ex);
        }
    }

    /// <summary>Disposes the pipeline of a removed cache entry if its build already completed successfully (an in-flight build disposes itself when it observes it was invalidated).</summary>
    private void DisposeCompleted(Lazy<Task<Result<Entry, AgentError>>> lazy)
    {
        if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result.IsSuccess)
        {
            lazy.Value.Result.Value.Dispose(_logger);
        }
    }

    /// <summary>A cached agent together with the pipeline the factory owns for it. Disposal is idempotent.</summary>
    private sealed class Entry(AgentDefinition definition, AIAgent agent, IChatClient client)
    {
        private int _disposed;

        public AgentDefinition Definition { get; } = definition;
        public AIAgent Agent { get; } = agent;

        public void Dispose(ILogger logger)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeClient(client, Definition.Name, logger);
            }
        }
    }

    [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "Disposing the chat-client pipeline for agent '{Agent}' failed: {Error}")]
    private static partial void LogPipelineDisposeFailed(ILogger logger, string agent, string error, Exception exception);

    [LoggerMessage(EventId = 122, Level = LogLevel.Warning, Message = "Building agent '{Agent}' failed: {Error}")]
    private static partial void LogBuildFailed(ILogger logger, string agent, string error, Exception exception);
}
