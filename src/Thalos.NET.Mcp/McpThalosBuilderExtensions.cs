using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Thalos.Mcp;

/// <summary>Registers MCP servers as Thalos tool sources.</summary>
public static class McpThalosBuilderExtensions
{
    /// <summary>Adds one MCP server as a tool source named <paramref name="name"/> (tools are exposed as <c>{name}__{tool}</c>).</summary>
    public static ThalosBuilder AddMcpServer(this ThalosBuilder builder, string name, McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definition);
        return builder.AddToolSource(sp => new McpToolSource(name, definition, sp.GetRequiredService<ILoggerFactory>()));
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

    /// <summary>Loads a Claude Code-style <c>.mcp.json</c>. Missing file → no servers (logged by the host if desired).</summary>
    public static ThalosBuilder AddMcpServersFromFile(this ThalosBuilder builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? builder.AddMcpServers(McpConfigFile.Load(path)) : builder;
    }
}
