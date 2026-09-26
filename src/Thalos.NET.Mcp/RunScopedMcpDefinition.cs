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
}
