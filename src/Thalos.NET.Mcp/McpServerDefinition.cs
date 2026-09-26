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

    /// <summary>
    /// stdio only: how long disposing the source waits before the server's process tree is killed. ModelContextProtocol
    /// 2.2.0 does not close the server's stdin on dispose; it waits this long and then kills the whole process tree, so every
    /// dispose of a stdio server takes the full timeout. Default 2 seconds (the SDK default is 5 s): a long value only slows
    /// host shutdown and run removal. In <c>.mcp.json</c> this is the <c>shutdownTimeout</c> property as a <c>hh:mm:ss</c>
    /// string (e.g. <c>"00:00:01"</c>).
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// stdio only: when set, each workflow run also gets its own copy of this server, started against the run's
    /// workspace by <see cref="RunMcpServerRegistry"/>. In <c>.mcp.json</c> this is the <c>runScoped</c> object.
    /// <see langword="null"/> means the server is host-wide only.
    /// </summary>
    public RunScopedMcpDefinition? RunScoped { get; set; }

    /// <summary><see cref="Type"/> lower-cased, or the default inferred from <see cref="Command"/>.</summary>
    public string EffectiveType => (Type ?? (Command is not null ? "stdio" : "http")).ToLowerInvariant();
}
