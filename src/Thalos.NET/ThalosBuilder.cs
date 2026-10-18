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
    /// <exception cref="ArgumentException">
    /// A type in <paramref name="toolTypes"/> is not marked <see cref="ThalosToolTypeAttribute"/>, or a remote run tool
    /// source named <paramref name="sourceName"/> was already added, such as Thalos.NET.Sandbox's <c>AddRemoteRunTools</c>
    /// or a remote MCP entry: the catalog keeps the first source of a name, so the two must never share one.
    /// </exception>
    /// <remarks>
    /// <see cref="LocalToolSource"/>'s own construction-time guard — which rejects a <see cref="ThalosToolAttribute"/>
    /// parameter whose shape carries a type assignable to <see cref="ISecurityContext"/> anywhere other than an
    /// exact, top-level, by-value match — does not run here. <see cref="LocalToolSource"/> is built lazily by the
    /// factory registered below, so that <see cref="ArgumentException"/> surfaces on the first resolution of
    /// <see cref="IToolSource"/>, not from this call.
    /// </remarks>
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

        ToolSourceNames.Of(Services).AddLocal(sourceName, nameof(sourceName));
        Services.AddSingleton<IToolSource>(sp => new LocalToolSource(sourceName, sp, toolTypes));
        return this;
    }

    /// <summary>
    /// In-process tools from the <see cref="ThalosToolTypeAttribute"/> class <typeparamref name="TTools"/>, exposed as
    /// <c>{sourceName}__{tool}</c>, without reflection: each tool is named by a binder that returns the tool method bound
    /// to an instance, as a method group, and arguments and results go through <paramref name="serializerOptions"/>. The
    /// form for trimmed and Native AOT hosts; <see cref="AddLocalTools(string, Type[])"/> discovers the same tools by
    /// reflection.
    /// </summary>
    /// <example><c>thalos.AddLocalTools&lt;MyTools&gt;("mine", MyJsonContext.Default.Options, t =&gt; t.Search, t =&gt; t.Fetch);</c></example>
    /// <typeparam name="TTools">The tool class; its public constructor is resolved from the container on every call.</typeparam>
    /// <param name="sourceName">The source name.</param>
    /// <param name="serializerOptions">
    /// The options the tools' arguments and results go through. Their resolver must cover every parameter and result
    /// type, typically a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>.
    /// </param>
    /// <param name="tools">One binder per tool, such as <c>t =&gt; t.ReadFile</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TTools"/> is not marked <see cref="ThalosToolTypeAttribute"/>, no binder is given, or a remote
    /// run tool source named <paramref name="sourceName"/> was already added.
    /// </exception>
    /// <remarks>
    /// The tools behave as <see cref="AddLocalTools(string, Type[])"/>'s do: names from <see cref="ThalosToolAttribute"/>,
    /// descriptions from <see cref="System.ComponentModel.DescriptionAttribute"/>, the caller bound to a parameter typed
    /// exactly <see cref="ISecurityContext"/>, and a fresh DI scope and instance per call. A binder that returns anything
    /// but one of <typeparamref name="TTools"/>'s own <see cref="ThalosToolAttribute"/> methods, or a parameter whose
    /// shape hides a principal, fails the source's first <see cref="IToolSource.GetToolsAsync"/>.
    /// </remarks>
    public ThalosBuilder AddLocalTools<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTools>(
        string sourceName, System.Text.Json.JsonSerializerOptions serializerOptions, params Func<TTools, Delegate>[] tools)
        where TTools : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(tools);
        if (!typeof(TTools).IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
        {
            throw new ArgumentException($"Type '{typeof(TTools).FullName}' is not marked [ThalosToolType].", nameof(tools));
        }

        if (tools.Length == 0)
        {
            throw new ArgumentException("At least one tool binder is required.", nameof(tools));
        }

        ToolSourceNames.Of(Services).AddLocal(sourceName, nameof(sourceName));
        Func<TTools, Delegate>[] binders = [.. tools];
        Services.AddSingleton<IToolSource>(sp => new LocalToolSource<TTools>(sourceName, sp, serializerOptions, binders));
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
