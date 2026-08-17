namespace Thalos;

/// <summary>A delivery channel (Telegram, WebSocket…). Phase 1.1 defines the seam only.</summary>
public interface IChannelAdapter
{
    /// <summary>Stable identifier of the channel (e.g. <c>telegram</c>).</summary>
    string ChannelId { get; }

    /// <summary>Delivers one turn event to the channel's endpoint for <paramref name="sessionId"/>.</summary>
    ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct);
}
