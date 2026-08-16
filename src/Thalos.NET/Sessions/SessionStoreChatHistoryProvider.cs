using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Thalos.Sessions;

/// <summary>
/// MAF chat-history provider backed by <see cref="IAgentSessionStore"/>. One instance serves all sessions;
/// the Thalos <see cref="SessionId"/> is stored in the MAF session's state bag under <see cref="StateKey"/>.
/// </summary>
public sealed class SessionStoreChatHistoryProvider(IAgentSessionStore store) : ChatHistoryProvider
{
    public const string StateKey = "thalos.session_id";

    /// <summary>Creates a fresh MAF session for <paramref name="agent"/> bound to Thalos session <paramref name="sessionId"/>.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance API by design: binding a MAF session to a Thalos session is a capability of the provider that owns the state key.")]
    public async ValueTask<AgentSession> CreateBoundSessionAsync(AIAgent agent, SessionId sessionId, CancellationToken ct)
    {
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        session.StateBag.SetValue(StateKey, sessionId.ToString());
        return session;
    }

    /// <summary>The bound Thalos session, or null for an unbound (stateless, one-shot) MAF session.</summary>
    public static SessionId? GetBoundSessionId(AgentSession session) =>
        session.StateBag.TryGetValue<string>(StateKey, out var raw) && SessionId.TryParse(raw, null, out var id) ? id : null;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is not { } session || GetBoundSessionId(session) is not { } id)
        {
            return []; // unbound → stateless run
        }

        var loaded = await store.LoadMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            throw new AgentTurnException(loaded.Error);
        }

        return loaded.Value;
    }

    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null || context.Session is not { } session || GetBoundSessionId(session) is not { } id)
        {
            return; // failed turn (runtime discards it) or unbound session: store nothing
        }

        var batch = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var stored = await store.AppendMessagesAsync(id, batch, cancellationToken).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            throw new AgentTurnException(stored.Error);
        }
    }
}
