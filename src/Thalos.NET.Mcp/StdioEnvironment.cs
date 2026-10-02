using ModelContextProtocol.Client;

namespace Thalos.Mcp;

/// <summary>
/// The environment a stdio MCP child starts with: the SDK's curated defaults, then the definition's
/// <see cref="McpServerDefinition.PassEnvironment"/> names read from the host, then its <c>env</c>, then a run's
/// <c>runScoped.env</c>. Never the host's whole environment (Thalos 0.13.0).
/// </summary>
internal static class StdioEnvironment
{
    public static Dictionary<string, string?> Build(
        McpServerDefinition definition, IReadOnlyDictionary<string, string?>? overrides, Func<string, string?> getHost)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in StdioClientTransportOptions.GetDefaultEnvironmentVariables())
        {
            env[key] = value;
        }

        foreach (var name in definition.PassEnvironment ?? [])
        {
            if (getHost(name) is { } value)
            {
                env[name] = value;
            }
        }

        foreach (var (key, value) in definition.Env ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            env[key] = value;
        }

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            env[key] = value;
        }

        return env;
    }
}
