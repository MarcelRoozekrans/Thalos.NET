using Microsoft.Extensions.AI;

namespace Thalos;

/// <summary>Creates the innermost <see cref="IChatClient"/> for an agent (Anthropic, OpenAI, a fake…).</summary>
public interface IChatClientProvider
{
    /// <summary>Provider name for diagnostics and telemetry tags (e.g. "anthropic").</summary>
    string Name { get; }

    /// <summary>Model id used when <see cref="AgentDefinition.Model"/> is null.</summary>
    string DefaultModel { get; }

    /// <summary>Creates the raw provider client for <paramref name="agent"/> (no decorators, no function invocation).</summary>
    IChatClient CreateChatClient(AgentDefinition agent);
}
