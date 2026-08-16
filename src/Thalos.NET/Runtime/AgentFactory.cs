using System.Collections.Concurrent;
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
/// Builds <c>provider → decorators (ascending Order) → ChatClientAgent</c>. MAF adds function invocation
/// outermost, so every decorator (e.g. AI.Sentinel) sees each model round-trip including tool results.
/// </summary>
/// <remarks>
/// <para>
/// The factory <em>owns</em> the chat-client pipelines it builds: the outermost decorated <see cref="IChatClient"/> is kept
/// with each cached agent and disposed on <see cref="Invalidate"/>, on replacement by a newer definition instance, and on
/// <see cref="Dispose"/>. Providers that want to share a transport across agents must do so internally (see
/// <see cref="IChatClientProvider.CreateChatClient"/>).
/// </para>
/// <para>
/// Builds are single-flight per <see cref="AgentId"/>: N concurrent first calls result in one
/// <see cref="IChatClientProvider.CreateChatClient"/> call and reference-equal agents. Failed builds are not cached.
/// </para>
/// <para>
/// A cached agent is reused while the supplied <see cref="AgentDefinition"/> is equal by value (id, name, description,
/// instructions, model, max output tokens, tool globs); a changed definition rebuilds and disposes the old pipeline.
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
    private readonly ConcurrentDictionary<AgentId, Lazy<Task<Result<Entry, AgentError>>>> _cache = new();
    private volatile bool _disposed;

    public AgentFactory(
        IChatClientProvider provider,
        IEnumerable<IChatClientDecorator> decorators,
        IToolCatalog toolCatalog,
        SessionStoreChatHistoryProvider historyProvider,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null)
    {
        _provider = provider;
        _decorators = decorators.OrderBy(d => d.Order).ToList();
        _toolCatalog = toolCatalog;
        _historyProvider = historyProvider;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<AgentFactory>() ?? NullLogger<AgentFactory>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct)
    {
        var retriedStaleFailure = false;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var lazy = _cache.GetOrAdd(definition.Id, static (_, state) =>
                new Lazy<Task<Result<Entry, AgentError>>>(() => state.self.BuildAsync(state.definition), LazyThreadSafetyMode.ExecutionAndPublication),
                (self: this, definition));
            var wasCompleted = lazy.IsValueCreated && lazy.Value.IsCompleted;

            var built = await AwaitBuildAsync(definition.Id, lazy, ct).ConfigureAwait(false);
            if (built.IsFailure)
            {
                _cache.TryRemove(KeyValuePair.Create(definition.Id, lazy)); // don't cache failures; next call retries
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
                _cache.TryRemove(KeyValuePair.Create(definition.Id, lazy));
                entry.Dispose(_logger); // completed after Dispose() swept the cache
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            if (!_cache.TryGetValue(definition.Id, out var current) || !ReferenceEquals(current, lazy))
            {
                entry.Dispose(_logger); // invalidated while building — rebuild
                continue;
            }

            if (!SameDefinition(entry.Definition, definition))
            {
                // The definition changed for a cached id: treat as invalidated and rebuild with the new one.
                if (_cache.TryRemove(KeyValuePair.Create(definition.Id, lazy)))
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
        if (_cache.TryRemove(agentId, out var lazy))
        {
            DisposeCompleted(lazy);
        }
    }

    /// <summary>Disposes every cached chat-client pipeline. Subsequent <see cref="GetOrCreateAsync"/> calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _disposed = true;
        while (!_cache.IsEmpty)
        {
            foreach (var id in _cache.Keys)
            {
                Invalidate(id);
            }
        }
    }

    /// <summary>Waits for a shared build with the caller's own token (the build itself is not cancelled by any single caller); a build that threw is evicted so it doesn't poison the cache.</summary>
    private async Task<Result<Entry, AgentError>> AwaitBuildAsync(AgentId id, Lazy<Task<Result<Entry, AgentError>>> lazy, CancellationToken ct)
    {
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully)
        {
            _cache.TryRemove(KeyValuePair.Create(id, lazy));
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
        && a.Tools.SequenceEqual(b.Tools, StringComparer.Ordinal);

    private async Task<Result<Entry, AgentError>> BuildAsync(AgentDefinition definition)
    {
        IChatClient? client = null;
        try
        {
            var tools = await _toolCatalog.ResolveAsync(definition, CancellationToken.None).ConfigureAwait(false);
            if (tools.IsFailure)
            {
                return Result<Entry, AgentError>.Failure(tools.Error);
            }

            client = _provider.CreateChatClient(definition);
            foreach (var decorator in _decorators)
            {
                client = decorator.Decorate(client, definition, _services);
            }

            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Id = definition.Id.ToString(),
                Name = definition.Name,
                Description = string.IsNullOrEmpty(definition.Description) ? null : definition.Description,
                ChatHistoryProvider = _historyProvider,
                ChatOptions = new ChatOptions
                {
                    Instructions = definition.Instructions,
                    ModelId = definition.Model ?? _provider.DefaultModel,
                    MaxOutputTokens = definition.MaxOutputTokens,
                    Tools = tools.Value.Count == 0 ? null : tools.Value.ToList(),
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

            return Result<Entry, AgentError>.Failure(AgentError.ProviderError($"Failed to build agent '{definition.Name}'.", ex.Message));
        }
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
}
