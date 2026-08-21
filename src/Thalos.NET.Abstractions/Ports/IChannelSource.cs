namespace Thalos;

/// <summary>
/// A source of inbound messages for one channel — the counterpart to <see cref="IChannelAdapter"/>. Implementations
/// stream until <c>ct</c> is cancelled and are responsible for their own authentication and filtering:
/// a message that reaches the pump has already been accepted by the channel.
/// </summary>
public interface IChannelSource
{
    /// <summary>Stable identifier of the channel; must match the <see cref="IChannelAdapter.ChannelId"/> that answers it.</summary>
    string ChannelId { get; }

    /// <summary>Streams inbound messages until cancelled.</summary>
    IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct);
}
