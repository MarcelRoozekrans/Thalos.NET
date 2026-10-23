using System.Text.Json;
using Thalos.Tools;

namespace Thalos.Git;

/// <summary>Registers <see cref="GitActionTools"/> on a <see cref="ThalosBuilder"/>.</summary>
public static class GitActionToolsThalosBuilderExtensions
{
    /// <summary>The tools' options: <see cref="GitActionToolsJsonContext"/>'s metadata with the settings a reflected tool's results are written with.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = LocalToolSources.CreateOptions(GitActionToolsJsonContext.Default);

    /// <summary>
    /// Adds <see cref="GitActionTools"/> as the source named <paramref name="sourceName"/>, without reflection:
    /// <c>{sourceName}__create_branch</c>, <c>{sourceName}__commit</c>, <c>{sourceName}__push</c> and
    /// <c>{sourceName}__open_pull_request</c>, in that order. The tools are what
    /// <c>AddLocalTools(sourceName, typeof(GitActionTools))</c> offers, with the same schemas and the same result text, and a
    /// trimmed or Native AOT host can use them. Register an <see cref="IGitWriteService"/> and an
    /// <see cref="IPullRequestPublisher"/> too; each call resolves both.
    /// </summary>
    /// <remarks>
    /// The tools' descriptions name each other as <c>git__commit</c>, <c>git__push</c> and <c>git__open_pull_request</c>,
    /// so <c>"git"</c> is the name they read right under.
    /// </remarks>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="sourceName">The source name, such as <c>"git"</c>.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceName"/> is blank or violates <see cref="ToolSourceName"/>, or a remote run tool source or a
    /// remote MCP entry of that name was already added.
    /// </exception>
    public static ThalosBuilder AddGitActionTools(this ThalosBuilder builder, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddLocalTools<GitActionTools>(
            sourceName,
            SerializerOptions,
            static t => t.CreateBranch,
            static t => t.Commit,
            static t => t.Push,
            static t => t.OpenPullRequest);
    }
}
