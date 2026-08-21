using ZeroAlloc.Results;

namespace Thalos.Channels;

/// <summary>
/// Stores which Thalos session is serving which external conversation. Implementations are singletons and must be
/// safe for concurrent use. A conversation that has never been bound is <c>null</c>, not an error — an unbound
/// conversation is the normal state of a first message.
/// </summary>
public interface IConversationMap
{
    /// <summary>The binding for <paramref name="conversationId"/> on <paramref name="channelId"/>, or null when unbound.</summary>
    ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct);

    /// <summary>Creates or replaces the binding.</summary>
    ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct);

    /// <summary>Removes the binding. Removing an absent binding succeeds.</summary>
    ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct);
}
