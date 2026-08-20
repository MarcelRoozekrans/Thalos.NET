using System.Threading.Channels;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>A channel that is both source and adapter, so a test can push a message in and read what came out.</summary>
public sealed class FakeChannel : IChannelSource, IChannelAdapter
{
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();
    private readonly Channel<InboundMessage> _secondaryInbound = Channel.CreateUnbounded<InboundMessage>();

    /// <inheritdoc />
    public string ChannelId => "fake";

    /// <summary>Every event delivered to this adapter, in delivery order.</summary>
    public List<AgentEvent> Delivered { get; } = [];

    /// <summary>The single conversation this fake uses for every message it sends.</summary>
    public ConversationId Conversation { get; } = new("c1");

    /// <summary>The caller identity attached to every message this fake sends.</summary>
    public ISecurityContext Caller { get; } = AnonymousSecurityContext.Instance;

    /// <summary>
    /// A second, independent <see cref="IChannelSource"/> for this same <see cref="ChannelId"/>, fed by
    /// <see cref="SendOnSecondary"/>. A pump built with both this and the primary source in its source list gives
    /// each its own reader loop, both resolving to this same adapter. That is what makes a genuine mid-turn
    /// <c>/cancel</c> observable in a test at all: one source's own reader loop handles its own messages strictly
    /// in order and can never dequeue a message for a conversation whose turn it is itself still blocked running —
    /// only a second, concurrently-running loop can deliver a <c>/cancel</c> while the first is still inside a turn.
    /// </summary>
    public IChannelSource Secondary { get; }

    public FakeChannel() => Secondary = new SecondarySource(this);

    /// <summary>Enqueues an inbound text message from <see cref="Caller"/> on <see cref="Conversation"/>.</summary>
    public void Send(string text) =>
        _inbound.Writer.TryWrite(new InboundMessage(ChannelId, Conversation, text, Caller, null));

    /// <summary>Enqueues an inbound text message onto <see cref="Secondary"/>'s reader loop, same conversation and caller.</summary>
    public void SendOnSecondary(string text) =>
        _secondaryInbound.Writer.TryWrite(new InboundMessage(ChannelId, Conversation, text, Caller, null));

    /// <summary>Completes the inbound stream, so a reader loop over <see cref="ReadAsync"/> finishes.</summary>
    public void Complete() => _inbound.Writer.TryComplete();

    /// <inheritdoc />
    public IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct) => _inbound.Reader.ReadAllAsync(ct);

    /// <inheritdoc />
    public ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct)
    {
        lock (Delivered)
        {
            Delivered.Add(agentEvent);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class SecondarySource(FakeChannel owner) : IChannelSource
    {
        public string ChannelId => owner.ChannelId;

        public IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct) => owner._secondaryInbound.Reader.ReadAllAsync(ct);
    }
}
