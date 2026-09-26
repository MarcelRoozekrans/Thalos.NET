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
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> violates <see cref="ToolSourceName"/>, a run-scoped entry named <paramref name="name"/> was
    /// already added, or <paramref name="definition"/> is incomplete/unsupported.
    /// </exception>
    public static ThalosBuilder AddMcpServer(this ThalosBuilder builder, string name, McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.RunScoped is not { } runScoped)
        {
            // works without AddLogging(): the MCP SDK and the source itself only need a factory, not a configured one
            return builder.AddToolSource(sp => new McpToolSource(name, definition, LoggerFactory(sp)));
        }

        var services = builder.Services;
        var runScopedServers = RunScopedServers(services);
        if (!runScopedServers.Definitions.TryAdd(name, definition))
        {
            throw new ArgumentException($"A run-scoped MCP server named '{name}' was already added.", nameof(name));
        }

        services.TryAddSingleton(sp => new RunMcpServerRegistry(
            runScopedServers.Definitions,
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

    private static ILoggerFactory LoggerFactory(IServiceProvider sp) => sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

    private static TimeProvider Clock(IServiceProvider sp) => sp.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>The collection's run-scoped entries, gathered across <see cref="AddMcpServer"/> calls for the one registry.</summary>
    private static RunScopedServerSet RunScopedServers(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(RunScopedServerSet))?.ImplementationInstance is RunScopedServerSet existing)
        {
            return existing;
        }

        var created = new RunScopedServerSet();
        services.AddSingleton(created);
        return created;
    }

    /// <summary>Every run-scoped entry added to one service collection, keyed by source name.</summary>
    private sealed class RunScopedServerSet
    {
        public Dictionary<string, McpServerDefinition> Definitions { get; } = new(StringComparer.Ordinal);
    }
}
