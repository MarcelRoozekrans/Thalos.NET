using System.Threading.Channels;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>A channel that is both source and adapter, so a test can push a message in and read what came out.</summary>
public sealed class FakeChannel : IChannelSource, IChannelAdapter
{
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();
    private Exception? _nextDeliverThrows;

    /// <inheritdoc />
    public string ChannelId => "fake";

    /// <summary>Every event delivered to this adapter, in delivery order.</summary>
    public List<AgentEvent> Delivered { get; } = [];

    /// <summary>The single conversation this fake uses for every message it sends.</summary>
    public ConversationId Conversation { get; } = new("c1");

    /// <summary>The caller identity attached to every message this fake sends.</summary>
    public ISecurityContext Caller { get; } = AnonymousSecurityContext.Instance;

    /// <summary>Enqueues an inbound text message from <see cref="Caller"/> on <see cref="Conversation"/>.</summary>
    public void Send(string text) => Send(text, Conversation);

    /// <summary>Enqueues an inbound text message from <see cref="Caller"/> on a specific conversation.</summary>
    public void Send(string text, ConversationId conversationId) =>
        _inbound.Writer.TryWrite(new InboundMessage(ChannelId, conversationId, text, Caller, null));

    /// <summary>Completes the inbound stream, so a reader loop over <see cref="ReadAsync"/> finishes.</summary>
    public void Complete() => _inbound.Writer.TryComplete();

    /// <summary>Makes the NEXT call to <see cref="DeliverAsync"/> throw <paramref name="ex"/> instead of recording the event.</summary>
    public void NextDeliverThrows(Exception ex) => _nextDeliverThrows = ex;

    /// <inheritdoc />
    public IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct) => _inbound.Reader.ReadAllAsync(ct);

    /// <inheritdoc />
    public ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct)
    {
        if (_nextDeliverThrows is { } ex)
        {
            _nextDeliverThrows = null;
            throw ex;
        }

        lock (Delivered)
        {
            Delivered.Add(agentEvent);
        }

        return ValueTask.CompletedTask;
    }
}
