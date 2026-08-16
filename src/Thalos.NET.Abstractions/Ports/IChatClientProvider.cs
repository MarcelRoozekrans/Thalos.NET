using Microsoft.Extensions.AI;

namespace Thalos;

/// <summary>Creates the innermost <see cref="IChatClient"/> for an agent (Anthropic, OpenAI, a fake…).</summary>
public interface IChatClientProvider
{
    string Name { get; }
    string DefaultModel { get; }
    IChatClient CreateChatClient(AgentDefinition agent);
}
