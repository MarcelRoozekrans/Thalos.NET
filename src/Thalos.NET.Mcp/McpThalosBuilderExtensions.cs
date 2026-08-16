using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Thalos.Mcp;

/// <summary>Registers MCP servers as Thalos tool sources.</summary>
public static class McpThalosBuilderExtensions
{
    /// <summary>Adds one MCP server as a tool source named <paramref name="name"/> (tools are exposed as <c>{name}__{tool}</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> violates <see cref="ToolSourceName"/> or <paramref name="definition"/> is incomplete/unsupported.</exception>
    public static ThalosBuilder AddMcpServer(this ThalosBuilder builder, string name, McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(definition);
        // works without AddLogging(): the MCP SDK and the source itself only need a factory, not a configured one
        return builder.AddToolSource(sp => new McpToolSource(name, definition, sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
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
}
