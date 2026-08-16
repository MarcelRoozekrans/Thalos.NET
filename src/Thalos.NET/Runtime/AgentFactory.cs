using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Thalos.Sessions;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Runtime;

/// <summary>
/// Builds <c>provider → decorators (ascending Order) → ChatClientAgent</c>. MAF adds function invocation
/// outermost, so every decorator (e.g. AI.Sentinel) sees each model round-trip including tool results.
/// </summary>
public sealed class AgentFactory(
    IChatClientProvider provider,
    IEnumerable<IChatClientDecorator> decorators,
    IToolCatalog toolCatalog,
    SessionStoreChatHistoryProvider historyProvider,
    IServiceProvider services,
    ILoggerFactory? loggerFactory) : IAgentFactory
{
    private readonly IReadOnlyList<IChatClientDecorator> _decorators = decorators.OrderBy(d => d.Order).ToList();
    private readonly ConcurrentDictionary<AgentId, AIAgent> _cache = new();

    public async ValueTask<Result<AIAgent, AgentError>> GetOrCreateAsync(AgentDefinition definition, CancellationToken ct)
    {
        if (_cache.TryGetValue(definition.Id, out var cached))
        {
            return Result<AIAgent, AgentError>.Success(cached);
        }

        var tools = await toolCatalog.ResolveAsync(definition, ct).ConfigureAwait(false);
        if (tools.IsFailure)
        {
            return Result<AIAgent, AgentError>.Failure(tools.Error);
        }

        IChatClient client;
        try
        {
            client = provider.CreateChatClient(definition);
            foreach (var decorator in _decorators)
            {
                client = decorator.Decorate(client, definition, services);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<AIAgent, AgentError>.Failure(AgentError.ProviderError($"Failed to build chat client for agent '{definition.Name}'.", ex.Message));
        }

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = definition.Id.ToString(),
            Name = definition.Name,
            Description = definition.Description,
            ChatHistoryProvider = historyProvider,
            ChatOptions = new ChatOptions
            {
                Instructions = definition.Instructions,
                ModelId = definition.Model ?? provider.DefaultModel,
                MaxOutputTokens = definition.MaxOutputTokens,
                Tools = tools.Value.Count == 0 ? null : tools.Value.ToList(),
            },
        }, loggerFactory, services);

        return Result<AIAgent, AgentError>.Success(_cache.GetOrAdd(definition.Id, agent));
    }

    public void Invalidate(AgentId agentId) => _cache.TryRemove(agentId, out _);
}
