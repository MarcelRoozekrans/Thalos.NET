using Microsoft.Extensions.AI;

namespace Thalos;

/// <summary>Creates the innermost <see cref="IChatClient"/> for an agent (Anthropic, OpenAI, a fake…).</summary>
/// <remarks>
/// Thalos persists chat history itself through a MAF <c>ChatHistoryProvider</c> and creates a fresh MAF session per turn, so
/// service-managed conversation ids are not persisted. Providers must not return <see cref="ChatResponse.ConversationId"/>:
/// MAF's <c>ChatClientAgentOptions.ThrowOnChatHistoryProviderConflict</c> (default <see langword="true"/>) throws when the
/// service claims to manage history while a history provider is configured.
/// </remarks>
public interface IChatClientProvider
{
    /// <summary>Provider name for diagnostics and telemetry tags (e.g. "anthropic").</summary>
    string Name { get; }

    /// <summary>Model id used when <see cref="AgentDefinition.Model"/> is null.</summary>
    string DefaultModel { get; }

    /// <summary>
    /// Creates the raw provider client for <paramref name="agent"/> (no decorators, no function invocation).
    /// The returned client is <em>owned by the caller</em> (the agent factory), which disposes it when the agent is
    /// invalidated, replaced or the factory is disposed. Providers that want to share an expensive transport
    /// (HTTP client, SDK client) across agents must reuse it internally and return a client whose disposal does not
    /// tear the shared transport down.
    /// </summary>
    IChatClient CreateChatClient(AgentDefinition agent);
}
