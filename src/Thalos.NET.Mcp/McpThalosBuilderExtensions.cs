using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workspaces;

namespace Thalos.Mcp;

/// <summary>Registers MCP servers as Thalos tool sources.</summary>
public static class McpThalosBuilderExtensions
{
    /// <summary>Adds one MCP server as a tool source named <paramref name="name"/> (tools are exposed as <c>{name}__{tool}</c>).</summary>
    /// <remarks>
    /// <para>
    /// An entry without <see cref="McpServerDefinition.RunScoped"/> is registered as an <see cref="McpToolSource"/>.
    /// </para>
    /// <para>
    /// An entry with it is registered as a <see cref="RunScopedMcpToolSource"/> over the host-wide server, and joins the
    /// one <see cref="RunMcpServerRegistry"/> shared by every run-scoped entry. That single instance is also registered
    /// as <see cref="IRunWorkspaceObserver"/>, <see cref="IRunWorkspaceChangeListener"/> and
    /// <see cref="IRunToolServerReadiness"/>, so the workspace provider starts and stops the very servers the sources
    /// route to. The registry finds the <see cref="IRunWorkspaceProvider"/> on first use, not when it is built, because
    /// the provider observes the registry; with none registered, run callers are refused and host callers served
    /// (ruling R7). Its clock is the registered <see cref="TimeProvider"/>, or <see cref="TimeProvider.System"/>.
    /// </para>
    /// <para>
    /// An entry whose <see cref="RunScopedMcpDefinition.Remote"/> is set is registered as a
    /// <see cref="RemoteRunToolSource.ForMcpHost"/> source over the host-wide server instead, and does not join the
    /// registry: a run's calls go to the endpoint the registered <see cref="IRunToolEndpointResolver"/> returns, bounded
    /// by the registered <see cref="RemoteRunToolOptions"/> or its defaults, and each registered
    /// <see cref="IRunToolCallObserver"/> is told of them. The source is also an <see cref="IRunWorkspaceObserver"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> violates <see cref="ToolSourceName"/>, a run-scoped entry shares <paramref name="name"/> with an MCP
    /// entry already added, in either order, or <paramref name="definition"/> is incomplete/unsupported, or is remote and
    /// also configures a local copy.
    /// </exception>
    public static ThalosBuilder AddMcpServer(this ThalosBuilder builder, string name, McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(definition);
        var services = builder.Services;
        var servers = McpServers(services);
        var runScoped = definition.RunScoped;
        if (servers.McpNames.TryGetValue(name, out var earlierRunScoped) && (earlierRunScoped || runScoped is not null))
        {
            // Plain with plain stays first-wins, as before. With a run-scoped entry either way round, the catalog's
            // first-wins could hand run callers the plain source, which is the host server.
            throw new ArgumentException(
                $"An MCP server named '{name}' was already added; a run-scoped entry cannot share its name with another MCP entry.", nameof(name));
        }

        servers.McpNames.TryAdd(name, runScoped is not null);
        if (runScoped is null)
        {
            // works without AddLogging(): the MCP SDK and the source itself only need a factory, not a configured one
            return builder.AddToolSource(sp => new McpToolSource(name, definition, LoggerFactory(sp)));
        }

        if (runScoped.Remote)
        {
            runScoped.ThrowIfInvalidRemote(name, nameof(definition));
            AddRemote(services, name, sp => RemoteRunToolSource.ForMcpHost(
                new McpToolSource(name, definition, LoggerFactory(sp)),
                new DeferredRunToolEndpointResolver(sp),
                sp.GetService<RemoteRunToolOptions>() ?? new RemoteRunToolOptions(),
                LoggerFactory(sp),
                Clock(sp),
                RunToolCallObservers(sp)));
            return builder;
        }

        servers.Definitions.Add(name, definition);
        services.TryAddSingleton(sp => new RunMcpServerRegistry(
            servers.Definitions,
            () => sp.GetService<IRunWorkspaceProvider>(), // deferred: the provider observes the registry, so it is built while the provider is
            LoggerFactory(sp),
            Clock(sp)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunWorkspaceObserver, RunMcpServerRegistry>(sp => sp.GetRequiredService<RunMcpServerRegistry>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunWorkspaceChangeListener, RunMcpServerRegistry>(sp => sp.GetRequiredService<RunMcpServerRegistry>()));
        services.TryAddSingleton<IRunToolServerReadiness>(sp => sp.GetRequiredService<RunMcpServerRegistry>());

        return builder.AddToolSource(sp => new RunScopedMcpToolSource(
            new McpToolSource(name, definition, LoggerFactory(sp)),
            runScoped,
            sp.GetRequiredService<RunMcpServerRegistry>(),
            Clock(sp),
            LoggerFactory(sp).CreateLogger<RunScopedMcpToolSource>()));
    }

    /// <summary>Adds every server in <paramref name="servers"/> (key = source name).</summary>
    public static ThalosBuilder AddMcpServers(this ThalosBuilder builder, IReadOnlyDictionary<string, McpServerDefinition> servers)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(servers);
        foreach (var (name, def) in servers)
        {
            builder.AddMcpServer(name, def);
        }

        return builder;
    }

    /// <summary>Loads a Claude Code-style <c>.mcp.json</c> and adds every server in it.</summary>
    /// <remarks>
    /// <paramref name="path"/> is resolved against the current working directory when relative — hosts should pass an absolute
    /// path such as <c>Path.Combine(env.ContentRootPath, ".mcp.json")</c>. A missing file is a silent no-op (no servers, nothing
    /// logged; check <see cref="File.Exists"/> yourself if you want to warn). <c>${VAR}</c> environment-variable expansion inside
    /// values is <em>not</em> implemented in 0.1 — values are used verbatim.
    /// </remarks>
    public static ThalosBuilder AddMcpServersFromFile(this ThalosBuilder builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? builder.AddMcpServers(McpConfigFile.Load(path)) : builder;
    }

    /// <summary>
    /// Registers one <see cref="RemoteRunToolSource"/>, made by <paramref name="factory"/> once, as the tool source named
    /// <paramref name="name"/> and as an <see cref="IRunWorkspaceObserver"/>, so a removed run's client is dropped.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The source's name, which keys the single instance.</param>
    /// <param name="factory">Makes the source; it should resolve <see cref="IRunToolEndpointResolver"/> and the observers lazily, through
    /// <see cref="DeferredRunToolEndpointResolver"/> and <see cref="RunToolCallObservers"/>, because the workspace provider that
    /// resolves endpoints also observes this source.</param>
    /// <exception cref="ArgumentException">A remote run tool source named <paramref name="name"/> was already added.</exception>
    internal static void AddRemote(IServiceCollection services, string name, Func<IServiceProvider, RemoteRunToolSource> factory)
    {
        if (services.Any(d => d.IsKeyedService && d.ServiceType == typeof(RemoteRunToolSource) && Equals(d.ServiceKey, name)))
        {
            // Two registrations under one key would resolve to one instance listed twice as a tool source.
            throw new ArgumentException($"A remote run tool source named '{name}' was already added.", nameof(name));
        }

        services.AddKeyedSingleton(name, (sp, _) => factory(sp));
        services.AddSingleton<IToolSource>(sp => sp.GetRequiredKeyedService<RemoteRunToolSource>(name));
        services.AddSingleton<IRunWorkspaceObserver>(sp => sp.GetRequiredKeyedService<RemoteRunToolSource>(name));
    }

    /// <summary>The registered <see cref="IRunToolCallObserver"/>s, resolved each time the sequence is enumerated, not when the source is built.</summary>
    /// <param name="sp">The container.</param>
    internal static IEnumerable<IRunToolCallObserver> RunToolCallObservers(IServiceProvider sp)
    {
        foreach (var observer in sp.GetServices<IRunToolCallObserver>())
        {
            yield return observer;
        }
    }

    internal static ILoggerFactory LoggerFactory(IServiceProvider sp) => sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

    internal static TimeProvider Clock(IServiceProvider sp) => sp.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>The collection's MCP entries, gathered across <see cref="AddMcpServer"/> calls.</summary>
    private static McpServerSet McpServers(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(McpServerSet))?.ImplementationInstance is McpServerSet existing)
        {
            return existing;
        }

        var created = new McpServerSet();
        services.AddSingleton(created);
        return created;
    }

    /// <summary>The MCP entries added to one service collection.</summary>
    private sealed class McpServerSet
    {
        /// <summary>Every MCP entry's source name, plain or run-scoped, mapped to whether the first entry by that name is run-scoped.</summary>
        public Dictionary<string, bool> McpNames { get; } = new(StringComparer.Ordinal);

        /// <summary>The run-scoped entries, keyed by source name, for the one registry.</summary>
        public Dictionary<string, McpServerDefinition> Definitions { get; } = new(StringComparer.Ordinal);
    }
}
