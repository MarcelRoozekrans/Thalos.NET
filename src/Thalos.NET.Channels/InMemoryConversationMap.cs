using System.Collections.Concurrent;
using ZeroAlloc.Results;

namespace Thalos.Channels;

/// <summary>In-process <see cref="IConversationMap"/>. The default, and all the console channel ever needs.</summary>
public sealed class InMemoryConversationMap : IConversationMap
{
    private readonly ConcurrentDictionary<(string Channel, string Conversation), ConversationBinding> _bindings = new();

    /// <inheritdoc />
    public ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct)
    {
        _bindings.TryGetValue((channelId, conversationId.Value), out var binding);
        return new(Result<ConversationBinding?, AgentError>.Success(binding));
    }

    /// <inheritdoc />
    public ValueTask<Result<ConversationBinding?, AgentError>> GetBySessionAsync(SessionId sessionId, CancellationToken ct)
    {
        // Linear over the live conversations: there is one per chat, and a host has a handful.
        foreach (var binding in _bindings.Values)
        {
            if (binding.SessionId == sessionId)
            {
                return new(Result<ConversationBinding?, AgentError>.Success(binding));
            }
        }

        return new(Result<ConversationBinding?, AgentError>.Success(null));
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[(binding.ChannelId, binding.ConversationId.Value)] = binding;
        return new(UnitResult<AgentError>.Success());
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct)
    {
        _bindings.TryRemove((channelId, conversationId.Value), out _);
        return new(UnitResult<AgentError>.Success());
    }
}
