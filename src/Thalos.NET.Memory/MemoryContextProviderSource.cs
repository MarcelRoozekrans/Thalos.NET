using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos.Runtime;

namespace Thalos.Memory;

/// <summary>
/// Creates a <see cref="MemoryContextProvider"/> per agent unless memory is disabled for it (<see cref="AgentMemorySettings.Enabled"/>
/// overrides <see cref="MemoryOptions.Enabled"/>). The provider gets its own <see cref="RecallOptions"/> copy — <see cref="AgentMemorySettings.TopK"/>
/// (floored to ≥ 1 here) over the host-wide <see cref="MemoryOptions.Recall"/> — so the bound options instance is never mutated.
/// Only the floor happens here: the upper bound (<see cref="MemoryQuery.MaxPageSize"/>, one page per scope partition) is enforced
/// at host start by <c>MemoryThalosBuilderExtensions</c>' <c>ValidateOnStart</c> registration, not by capping the value in this class.
/// </summary>
public sealed class MemoryContextProviderSource(
    IMemoryService memory,
    IOptions<MemoryOptions> options,
    TimeProvider clock,
    AgentEventHub hub,
    IUntrustedContentScanner? scanner = null,
    ILoggerFactory? loggerFactory = null) : IAgentContextProviderSource
{
    /// <inheritdoc />
    public AIContextProvider? CreateProvider(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var o = options.Value;
        if (!(agent.Memory?.Enabled ?? o.Enabled))
        {
            return null;
        }

        var recall = new RecallOptions { TopK = Math.Max(1, agent.Memory?.TopK ?? o.Recall.TopK), MinScore = o.Recall.MinScore, MaxChars = o.Recall.MaxChars };
        return new MemoryContextProvider(memory, agent.Id, recall, o.SharedOwnerId, clock, hub, scanner, loggerFactory?.CreateLogger<MemoryContextProvider>());
    }
}
