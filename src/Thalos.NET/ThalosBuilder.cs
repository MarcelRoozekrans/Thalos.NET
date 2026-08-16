using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Sessions;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>Fluent configuration surface returned by <see cref="ThalosServiceCollectionExtensions.AddThalos"/>. Provider/Sentinel/Mcp packages add extension methods on this type.</summary>
public sealed class ThalosBuilder
{
    private static readonly AgentDefinitionValidator Validator = new(); // generated validator is stateless

    internal ThalosBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The underlying service collection, for registrations the builder does not cover.</summary>
    public IServiceCollection Services { get; }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        Services.Configure<ThalosOptions>(o => o.ToolPolicies.Add(new ToolPolicyBinding(toolPattern, policyName)));
        return this;
    }

    /// <summary>Registers an <see cref="IAuthorizationPolicy"/> (looked up by its <c>[Policy]</c> name). Registering the same type twice is a no-op.</summary>
    public ThalosBuilder AddPolicy<TPolicy>() where TPolicy : class, IAuthorizationPolicy
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationPolicy, TPolicy>());
        return this;
    }

    /// <summary>Uses <paramref name="provider"/> as the (single) chat-client provider, replacing any earlier one.</summary>
    public ThalosBuilder UseChatClientProvider(IChatClientProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.Replace(ServiceDescriptor.Singleton(provider));
        return this;
    }

    /// <summary>Uses <typeparamref name="TProvider"/> as the (single) chat-client provider, replacing any earlier one.</summary>
    public ThalosBuilder UseChatClientProvider<TProvider>() where TProvider : class, IChatClientProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IChatClientProvider, TProvider>());
        return this;
    }

    /// <summary>Adds a chat-client decorator (applied in ascending <see cref="IChatClientDecorator.Order"/>). Registering the same type twice is a no-op.</summary>
    public ThalosBuilder AddChatClientDecorator<TDecorator>() where TDecorator : class, IChatClientDecorator
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientDecorator, TDecorator>());
        return this;
    }

    /// <summary>Adds a tool source resolved from the container. Registering the same type twice is a no-op.</summary>
    public ThalosBuilder AddToolSource<TSource>() where TSource : class, IToolSource
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolSource, TSource>());
        return this;
    }

    /// <summary>Adds a tool source instance.</summary>
    public ThalosBuilder AddToolSource(IToolSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.AddSingleton(source);
        return this;
    }

    /// <summary>Adds a tool source created by <paramref name="factory"/> once the container is built.</summary>
    public ThalosBuilder AddToolSource(Func<IServiceProvider, IToolSource> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>In-process tools from <see cref="ThalosToolTypeAttribute"/> classes, exposed as <c>{sourceName}__{tool}</c>.</summary>
    /// <exception cref="ArgumentException">A type in <paramref name="toolTypes"/> is not marked <see cref="ThalosToolTypeAttribute"/>.</exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public ThalosBuilder AddLocalTools(string sourceName, params Type[] toolTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(toolTypes);
        foreach (var type in toolTypes)
        {
            if (!type.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
            {
                throw new ArgumentException($"Type '{type.FullName}' is not marked [ThalosToolType].", nameof(toolTypes));
            }
        }

        Services.AddSingleton<IToolSource>(sp => new LocalToolSource(sourceName, sp, toolTypes));
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="TStore"/> as the session store, wrapped in the ZeroAlloc.Telemetry proxy (<c>AgentSessionStoreInstrumented</c>).
    /// The store is a singleton: implementations that need scoped resources (a DbContext, a unit of work) should take
    /// <see cref="IServiceScopeFactory"/> and create a scope per call rather than depending on scoped services directly.
    /// </summary>
    public ThalosBuilder UseSessionStore<TStore>() where TStore : class, IAgentSessionStore
    {
        Services.Replace(ServiceDescriptor.Singleton<TStore, TStore>());
        Services.Replace(ServiceDescriptor.Singleton<IAgentSessionStore>(sp => new AgentSessionStoreInstrumented(sp.GetRequiredService<TStore>())));
        return this;
    }

    /// <summary>Uses the built-in <see cref="InMemorySessionStore"/> (development/testing).</summary>
    public ThalosBuilder UseInMemorySessionStore() => UseSessionStore<InMemorySessionStore>();
}
