using AI.Sentinel;
using Microsoft.Extensions.AI;

namespace Thalos.Sentinel;

/// <summary>Registers AI.Sentinel closest to the model (Order 1000 = outermost decorator; still inside MAF's function-invocation loop, so every round-trip is scanned).</summary>
public sealed class SentinelChatClientDecorator : IChatClientDecorator
{
    public int Order => 1000;

    public IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services)
    {
        var sentinel = new ChatClientBuilder(inner).UseAISentinel().Build(services);
        return new SentinelErrorMappingChatClient(sentinel);
    }
}
