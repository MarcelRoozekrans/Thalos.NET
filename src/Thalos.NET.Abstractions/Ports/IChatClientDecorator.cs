using Microsoft.Extensions.AI;

namespace Thalos;

/// <summary>
/// Wraps the provider client. Lower <see cref="Order"/> = closer to the provider (innermost).
/// AI.Sentinel registers here. Function invocation is added by MAF outside all decorators.
/// </summary>
public interface IChatClientDecorator
{
    int Order { get; }
    IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services);
}
