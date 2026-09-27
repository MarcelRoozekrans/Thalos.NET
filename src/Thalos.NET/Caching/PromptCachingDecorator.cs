using Microsoft.Extensions.AI;

namespace Thalos.Caching;

/// <summary>
/// Registers <see cref="PromptCachingChatClient"/> outermost among the decorators (<see cref="int.MaxValue"/>), so every
/// decorator further in, such as AI.Sentinel at -1000, sees the hinted request. MAF's function-invocation loop sits
/// outside all decorators, so the hints are placed afresh on every model round trip of a turn.
/// </summary>
internal sealed class PromptCachingDecorator : IChatClientDecorator
{
    /// <inheritdoc />
    public int Order => int.MaxValue;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services) => new PromptCachingChatClient(inner);
}
