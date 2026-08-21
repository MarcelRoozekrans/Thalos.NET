namespace Thalos;

/// <summary>A delivery channel (Telegram, WebSocket…). Phase 1.1 defines the seam only.</summary>
public interface IChannelAdapter
{
    /// <summary>Stable identifier of the channel (e.g. <c>telegram</c>).</summary>
    string ChannelId { get; }

    /// <summary>
    /// Delivers one turn event to <paramref name="conversationId"/> on this channel.
    /// </summary>
    /// <remarks>
    /// Keyed on the CONVERSATION, not the session, because that is what an adapter can actually address: a chat, a
    /// socket, a terminal. Much of what a channel must say belongs to a conversation that has no session at all —
    /// <c>/help</c>, an unrecognised command, "still working on the previous message", "that session had already
    /// ended" — and a session-keyed seam can only deliver those by inventing a session id that resolves to nothing.
    /// <paramref name="agentEvent"/> still carries its own <see cref="AgentEvent.SessionId"/>, so an adapter that
    /// wants to correlate a delivery with a session reads it from there.
    /// </remarks>
    /// <param name="conversationId">The conversation to deliver to, as supplied by this channel's <see cref="IChannelSource"/>.</param>
    /// <param name="agentEvent">The event to render.</param>
    /// <param name="ct">A token to cancel the delivery.</param>
    ValueTask DeliverAsync(ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct);
}
