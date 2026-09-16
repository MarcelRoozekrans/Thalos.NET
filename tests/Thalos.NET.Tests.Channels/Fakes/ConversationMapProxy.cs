using Thalos.Channels;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>
/// Sits in front of a real <see cref="InMemoryConversationMap"/> so a test can make the NEXT <see cref="BindAsync"/>
/// call fail without a full substitute — <see cref="GetAsync"/> and <see cref="UnbindAsync"/> still hit the real
/// map underneath, so every assertion the other pump tests already make against <c>PumpHarness.Map</c> keeps
/// working unchanged.
/// </summary>
internal sealed class ConversationMapProxy(InMemoryConversationMap inner) : IConversationMap
{
    private AgentError? _nextBindFailure;

    /// <summary>Makes the NEXT <see cref="BindAsync"/> call fail with <paramref name="error"/> instead of binding.</summary>
    public void FailNextBind(AgentError error) => _nextBindFailure = error;

    /// <inheritdoc />
    public ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct) =>
        inner.GetAsync(channelId, conversationId, ct);

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct)
    {
        if (_nextBindFailure is { } error)
        {
            _nextBindFailure = null;
            return new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Failure(error));
        }

        return inner.BindAsync(binding, ct);
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct) =>
        inner.UnbindAsync(channelId, conversationId, ct);
}
