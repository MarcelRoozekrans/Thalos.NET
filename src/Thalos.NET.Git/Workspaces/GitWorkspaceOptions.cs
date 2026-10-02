namespace Thalos.Git.Workspaces;

/// <summary>Configuration for <see cref="GitWorktreeWorkspaceProvider"/>.</summary>
public sealed class GitWorkspaceOptions
{
    /// <summary>
    /// Absolute. Mirrors live under <c>&lt;DataRoot&gt;/mirrors/&lt;repository&gt;</c>, worktrees under
    /// <c>&lt;DataRoot&gt;/runs/&lt;run-id&gt;</c>, and each worktree's sidecar record under
    /// <c>&lt;DataRoot&gt;/runs/&lt;run-id&gt;.workspace.json</c> — outside the worktree, so no tool operating
    /// inside the workspace can read or edit it. <see cref="GitWorkspaceThalosBuilderExtensions.UseGitWorktreeWorkspaces"/>
    /// rejects a blank or relative value.
    /// </summary>
    public string DataRoot { get; set; } = "";

    /// <summary>The git executable to run. Resolved via <c>PATH</c> when it is a bare name, as the default <c>"git"</c> is.</summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>
    /// The environment every git process starts from, instead of this process's own. Null, the default, inherits this
    /// process's environment, as a trusted host's git needs its proxy and certificate settings. A host whose environment
    /// holds a secret a git child must not see, such as the sandbox host's token, passes a curated set here. Either way
    /// <see cref="GitCli"/> then strips every inherited <c>GIT_*</c> variable and sets its own isolation variables.
    /// </summary>
    public IReadOnlyDictionary<string, string>? BaseEnvironment { get; set; }

    /// <summary>How long a single git invocation may run before <see cref="GitCli"/> kills it. Default 5 minutes.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
