namespace Thalos.Mcp;

/// <summary>One MCP server. Same JSON shape as Claude Code's <c>.mcp.json</c> entries.</summary>
public sealed class McpServerDefinition
{
    /// <summary>"stdio" | "http" | "sse". Defaults to "stdio" when <see cref="Command"/> is set, else "http".</summary>
    public string? Type { get; set; }

    /// <summary>Executable to launch (stdio).</summary>
    public string? Command { get; set; }

    /// <summary>Command-line arguments (stdio).</summary>
    public IReadOnlyList<string>? Args { get; set; }

    /// <summary>Extra environment variables for the child process (stdio).</summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }

    /// <summary>Working directory for the child process (stdio).</summary>
    public string? Cwd { get; set; }

    /// <summary>Endpoint (http/sse).</summary>
    public string? Url { get; set; }

    /// <summary>Additional request headers (http/sse).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    /// <summary>Connect + list-tools timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><see cref="Type"/> lower-cased, or the default inferred from <see cref="Command"/>.</summary>
    public string EffectiveType => (Type ?? (Command is not null ? "stdio" : "http")).ToLowerInvariant();
}
