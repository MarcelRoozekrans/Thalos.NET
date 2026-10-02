namespace Thalos.Mcp;

/// <summary>
/// The <c>runScoped</c> object of an <c>.mcp.json</c> stdio entry: how <see cref="RunMcpServerRegistry"/> starts one
/// private copy of that server per workflow run, against the run's own workspace. The host entry's
/// <see cref="McpServerDefinition.Command"/>, <see cref="McpServerDefinition.Timeout"/> and
/// <see cref="McpServerDefinition.ShutdownTimeout"/> are reused as they are.
/// </summary>
/// <remarks>
/// In <see cref="Args"/>, <see cref="Env"/> values and <see cref="Cwd"/>, the placeholders <c>${run.id}</c>,
/// <c>${run.workspace.root}</c> and <c>${run.workspace.solution}</c> are replaced with the run's id, its workspace
/// root and its solution path. Any other <c>${...}</c> is left verbatim. A <c>${run.workspace.solution}</c> for a
/// workspace with no solution fails that run's start.
/// </remarks>
public sealed class RunScopedMcpDefinition
{
    /// <summary>
    /// Replaces the host entry's <see cref="McpServerDefinition.Args"/> for a run's server, after substitution.
    /// <see langword="null"/> keeps the host entry's arguments, also substituted.
    /// </summary>
    public IReadOnlyList<string>? Args { get; set; }

    /// <summary>
    /// Environment variables for a run's server, after substitution, layered over the host entry's
    /// <see cref="McpServerDefinition.Env"/>: a key set here wins over the same key there.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }

    /// <summary>Working directory for a run's server, after substitution. <see langword="null"/> means <c>${run.workspace.root}</c>.</summary>
    public string? Cwd { get; set; }

    /// <summary>
    /// A tool, called with no arguments every two seconds after the server lists its tools, until a call does not come
    /// back as an error; only then is the server ready. <see langword="null"/> means ready as soon as the tool list
    /// returns.
    /// </summary>
    public string? ReadyTool { get; set; }

    /// <summary>
    /// What happens before the next routed call once files in the run's workspace have changed: <c>"none"</c>,
    /// <c>"tool:&lt;name&gt;"</c> to call that tool with no arguments, or <c>"restart"</c> to stop the server and start
    /// a new one.
    /// </summary>
    /// <remarks>
    /// A reload, like the first start, makes a build-aware server such as Roslyn re-evaluate the workspace's build files,
    /// and MSBuild runs code from those files when it evaluates them. A reload is therefore only safe while the run's
    /// writable extensions exclude MSBuild files (<c>.csproj</c>, <c>.props</c>, <c>.targets</c> and the like), so
    /// every build file it evaluates came from the repository. This registry adds no protection of its own.
    /// </remarks>
    public string Reload { get; set; } = "none";

    /// <summary>
    /// How long one routed call waits for the run's server to take it: a start still in progress, a pending reload or
    /// restart, and the calls such a reload first waits out. On expiry the call is refused with an error result; the
    /// server keeps starting or reloading. Default two minutes. In <c>.mcp.json</c> this is <c>readyWaitTimeout</c> as a
    /// <c>hh:mm:ss</c> string. Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    /// <remarks>
    /// It bounds the caller's wait only, never the start or the reload: those belong to the server, so a reload that
    /// takes longer still completes, and the calls after it are served. A host normally waits for the run's servers with
    /// <see cref="IRunToolServerReadiness.WaitAllReadyAsync"/> before it dispatches the run's work, so in practice this
    /// bounds the wait for reloads that file changes cause, which re-load the workspace. Set it above the server's own
    /// load time for the largest workspace it serves, or calls made during a reload are refused until it finishes.
    /// </remarks>
    public TimeSpan ReadyWaitTimeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// How long one routed call may run on the run's server. A call holds its server's lease, and a reload waits for every
    /// lease, so an unbounded call would hold that server's reloads back. On expiry the call is cancelled and an error
    /// result is returned. Default two minutes. In <c>.mcp.json</c> this is <c>callTimeout</c> as a <c>hh:mm:ss</c>
    /// string. Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = DefaultTimeout;

    /// <summary>The default of <see cref="ReadyWaitTimeout"/> and <see cref="CallTimeout"/>.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When true, a run's copy of this server is not started on this host: a run's calls go to the run's own remote tool
    /// endpoint, which <see cref="Thalos.Workspaces.IRunToolEndpointResolver"/> finds, through
    /// <see cref="RemoteRunToolSource"/>. The host entry still supplies the tool schemas and serves callers with no run.
    /// <see cref="Args"/>, <see cref="Env"/>, <see cref="Cwd"/> and <see cref="ReadyTool"/> must then be
    /// <see langword="null"/>, <see cref="Reload"/> <c>"none"</c> and <see cref="ReadyWaitTimeout"/> its default: they
    /// describe a local copy, which a remote entry does not have. <see cref="CallTimeout"/> bounds each remote call; the
    /// registered <see cref="RemoteRunToolOptions"/>, or its defaults, give the connect and observer bounds. In
    /// <c>.mcp.json</c> this is <c>remote</c>.
    /// </summary>
    public bool Remote { get; set; }

    /// <summary>Rejects a <see cref="Remote"/> entry that also describes a local copy, or whose call timeout a timer cannot hold.</summary>
    /// <param name="name">The entry's name, for the message.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">One of the local-copy settings is set, or <see cref="CallTimeout"/> is invalid.</exception>
    internal void ThrowIfInvalidRemote(string name, string paramName)
    {
        RunMcpServerRegistry.ThrowIfInvalidTimeouts(name, this, paramName);
        var local = new List<string>();
        if (Args is not null)
        {
            local.Add("args");
        }

        if (Env is not null)
        {
            local.Add("env");
        }

        if (Cwd is not null)
        {
            local.Add("cwd");
        }

        if (ReadyTool is not null)
        {
            local.Add("readyTool");
        }

        if (!string.Equals(Reload, "none", StringComparison.Ordinal))
        {
            local.Add("reload");
        }

        if (ReadyWaitTimeout != DefaultTimeout)
        {
            local.Add("readyWaitTimeout");
        }

        if (local.Count > 0)
        {
            throw new ArgumentException(
                $"Run-scoped MCP server '{name}' is remote, so it has no local copy to configure; remove {string.Join(", ", local)}.", paramName);
        }
    }
}
