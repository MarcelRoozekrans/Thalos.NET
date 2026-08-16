namespace Thalos;

/// <summary>A delivery channel (Telegram, WebSocket…). Phase 1.1 defines the seam only.</summary>
public interface IChannelAdapter
{
    string ChannelId { get; }
    ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct);
}
