using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Thalos.Sessions;

/// <summary>
/// MAF chat-history provider backed by <see cref="IAgentSessionStore"/>. One instance serves all sessions;
/// the Thalos <see cref="SessionId"/> is stored in the MAF session's state bag under <see cref="StateKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// The runtime creates a fresh MAF <see cref="AgentSession"/> per turn (bound via <see cref="CreateBoundSessionAsync"/>),
/// so nothing else in the MAF state bag survives a turn — in particular, service-managed conversation ids are not persisted.
/// Chat-client providers must therefore not return <see cref="ChatResponse.ConversationId"/>: MAF's
/// <c>ChatClientAgentOptions.ThrowOnChatHistoryProviderConflict</c> (default <see langword="true"/>) throws when the service
/// claims to manage history while a <see cref="ChatHistoryProvider"/> is configured.
/// </para>
/// <para>
/// A MAF session without <see cref="StateKey"/> is <em>unbound</em>: history is empty and nothing is stored (stateless
/// one-shot run). A session whose <see cref="StateKey"/> value is present but not a valid <see cref="SessionId"/> is treated
/// as a corrupt binding and fails the turn with <see cref="AgentTurnException"/> (<see cref="AgentErrorCode.StoreError"/>).
/// </para>
/// </remarks>
public sealed class SessionStoreChatHistoryProvider(IAgentSessionStore store) : ChatHistoryProvider
{
    /// <summary>State-bag key under which the bound Thalos <see cref="SessionId"/> (ULID string) is stored in a MAF <see cref="AgentSession"/>.</summary>
    public const string StateKey = "thalos.session_id";

    private static readonly string[] Keys = [StateKey];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => Keys;

    /// <summary>Creates a fresh MAF session for <paramref name="agent"/> bound to Thalos session <paramref name="sessionId"/>.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance API by design: binding a MAF session to a Thalos session is a capability of the provider that owns the state key.")]
    public async ValueTask<AgentSession> CreateBoundSessionAsync(AIAgent agent, SessionId sessionId, CancellationToken ct)
    {
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        session.StateBag.SetValue(StateKey, sessionId.ToString());
        return session;
    }

    /// <summary>The bound Thalos session, or null for an unbound (stateless, one-shot) MAF session.</summary>
    /// <exception cref="AgentTurnException"><see cref="StateKey"/> is present but its value is not a valid <see cref="SessionId"/>.</exception>
    public static SessionId? GetBoundSessionId(AgentSession session)
    {
        if (!session.StateBag.TryGetValue<string>(StateKey, out var raw))
        {
            return null;
        }

        if (SessionId.TryParse(raw, null, out var id))
        {
            return id;
        }

        throw new AgentTurnException(AgentError.StoreError("Corrupt session binding.", raw));
    }

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
