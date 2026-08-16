using Microsoft.Extensions.AI;

namespace Thalos;

/// <summary>
/// Wraps the provider client. Lower <see cref="Order"/> = closer to the provider (innermost).
/// AI.Sentinel registers here. Function invocation is added by MAF outside all decorators.
/// </summary>
public interface IChatClientDecorator
{
    /// <summary>Sort key: lower = closer to the provider (innermost); ties keep registration order.</summary>
    int Order { get; }

    /// <summary>Returns a client that wraps <paramref name="inner"/> for <paramref name="agent"/>; may resolve dependencies from <paramref name="services"/>.</summary>
    IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services);
}
