using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>
/// One message arriving from a channel. <paramref name="Caller"/> is supplied by the channel — Thalos never infers an
/// identity — and <paramref name="ExternalMessageId"/> is the transport's own id for the message, where it has one.
/// </summary>
public sealed record InboundMessage(
    string ChannelId,
    ConversationId ConversationId,
    string Text,
    ISecurityContext Caller,
    string? ExternalMessageId);
