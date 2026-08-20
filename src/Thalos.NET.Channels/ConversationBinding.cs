namespace Thalos.Channels;

/// <summary>Binds one external conversation to the Thalos session currently serving it.</summary>
public sealed record ConversationBinding(
    string ChannelId,
    ConversationId ConversationId,
    SessionId SessionId,
    AgentId AgentId,
    DateTimeOffset LastActivityAt);
