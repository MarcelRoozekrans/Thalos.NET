using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Sessions;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>Fluent configuration surface. Provider/Sentinel/Mcp packages add extension methods on this type.</summary>
public sealed class ThalosBuilder(IServiceCollection services)
{
    private static readonly AgentDefinitionValidator Validator = new(); // generated validator is stateless

    /// <summary>The underlying service collection, for registrations the builder does not cover.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>Adds an agent definition (validated eagerly) to <see cref="ThalosOptions.Agents"/>.</summary>
    public ThalosBuilder AddAgent(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = Validator.Validate(definition);
        if (!validation.IsValid)
        {
            var first = validation.Failures[0];
            throw new ArgumentException($"Invalid agent definition '{definition.Name}': {first.PropertyName} — {first.ErrorMessage}", nameof(definition));
        }

        Services.Configure<ThalosOptions>(o => o.Agents.Add(definition));
        return this;
    }

    /// <summary>Requires the <c>[Policy]</c> named <paramref name="policyName"/> to pass for tools whose qualified name matches <paramref name="toolPattern"/>.</summary>
    public ThalosBuilder RequireToolPolicy(string toolPattern, string policyName)
    {
        Services.Configure<ThalosOptions>(o => o.ToolPolicies.Add(new ToolPolicyBinding(toolPattern, policyName)));
        return this;
    }

    /// <summary>Registers an <see cref="IAuthorizationPolicy"/> (looked up by its <c>[Policy]</c> name).</summary>
    public ThalosBuilder AddPolicy<TPolicy>() where TPolicy : class, IAuthorizationPolicy
    {
        Services.AddSingleton<IAuthorizationPolicy, TPolicy>();
        return this;
    }

    /// <summary>Uses <paramref name="provider"/> as the (single) chat-client provider, replacing any earlier one.</summary>
    public ThalosBuilder UseChatClientProvider(IChatClientProvider provider)
    {
        Services.Replace(ServiceDescriptor.Singleton(provider));
        return this;
    }

    /// <summary>Uses <typeparamref name="TProvider"/> as the (single) chat-client provider, replacing any earlier one.</summary>
    public ThalosBuilder UseChatClientProvider<TProvider>() where TProvider : class, IChatClientProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IChatClientProvider, TProvider>());
        return this;
    }

    /// <summary>Adds a chat-client decorator (applied in ascending <see cref="IChatClientDecorator.Order"/>).</summary>
    public ThalosBuilder AddChatClientDecorator<TDecorator>() where TDecorator : class, IChatClientDecorator
    {
        Services.AddSingleton<IChatClientDecorator, TDecorator>();
        return this;
    }

    /// <summary>Adds a tool source resolved from the container.</summary>
    public ThalosBuilder AddToolSource<TSource>() where TSource : class, IToolSource
    {
        Services.AddSingleton<IToolSource, TSource>();
        return this;
    }

    /// <summary>Adds a tool source instance.</summary>
    public ThalosBuilder AddToolSource(IToolSource source)
    {
        Services.AddSingleton(source);
        return this;
    }

    /// <summary>In-process tools from <see cref="ThalosToolTypeAttribute"/> classes.</summary>
    public ThalosBuilder AddLocalTools(string sourceName, params Type[] toolTypes)
    {
        Services.AddSingleton<IToolSource>(sp => new LocalToolSource(sourceName, sp, toolTypes));
        return this;
    }

    /// <summary>Uses <typeparamref name="TStore"/> as the session store, wrapped in the ZeroAlloc.Telemetry proxy (<c>AgentSessionStoreInstrumented</c>).</summary>
    public ThalosBuilder UseSessionStore<TStore>() where TStore : class, IAgentSessionStore
    {
        Services.Replace(ServiceDescriptor.Singleton<TStore, TStore>());
        Services.Replace(ServiceDescriptor.Singleton<IAgentSessionStore>(sp => new AgentSessionStoreInstrumented(sp.GetRequiredService<TStore>())));
        return this;
    }

    /// <summary>Uses the built-in <see cref="InMemorySessionStore"/> (development/testing).</summary>
    public ThalosBuilder UseInMemorySessionStore() => UseSessionStore<InMemorySessionStore>();
}
