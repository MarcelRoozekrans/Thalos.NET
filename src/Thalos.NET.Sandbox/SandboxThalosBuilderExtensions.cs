using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Workspaces;

namespace Thalos.Sandbox;

/// <summary>Registers <see cref="SandboxRunWorkspaceProvider"/> on a <see cref="ThalosBuilder"/>.</summary>
public static class SandboxThalosBuilderExtensions
{
    /// <summary>
    /// Makes every run sandboxed: <see cref="SandboxRunWorkspaceProvider"/> replaces the run workspace provider, the base
    /// file reader, the tool endpoint resolver, the parkable provider, the hand-off and the run tool server readiness,
    /// and the <c>workspace</c> and <c>sandbox</c> tools are served by each run's sandbox. An
    /// <see cref="ISandboxRuntime"/> must be registered too, for example by <c>UseDockerSandboxRuntime</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One mirror.</b> A single <see cref="GitWorkspaceOptions"/> rooted at <c>&lt;DataRoot&gt;/publish</c> is shared by
    /// the <see cref="GitMirrorStore"/>, the <see cref="GitPatchApplier"/> and the <see cref="GitWorktreeWorkspaceProvider"/>
    /// inside <see cref="SandboxPublishWorktrees"/>, which has no observers and the same <see cref="IGitCredentialSource"/>.
    /// So a sandbox's bundle and its publish worktree come from one mirror under one lock, and the base commit a sandbox
    /// started from is present when its publish worktree is cut, even if the default branch was force-pushed meanwhile.
    /// </para>
    /// <para>
    /// <b>Not <c>UseRunWorkspaceTools</c>.</b> On the API the workspace tools are remote: registering the local ones
    /// would serve a run's file writes on the host.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="configure">Sets <see cref="SandboxOptions"/>.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="SandboxOptions.DataRoot"/> is not absolute, <see cref="SandboxOptions.Image"/> is blank, or
    /// <see cref="SandboxOptions.ProtectedPaths"/> is empty or holds a <c>..</c> segment.
    /// </exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder UseSandboxRunWorkspaces(this ThalosBuilder builder, Action<SandboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SandboxOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.DataRoot) || !Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException($"SandboxOptions.DataRoot must be an absolute path (was '{options.DataRoot}').", nameof(configure));
        }

        if (string.IsNullOrWhiteSpace(options.Image))
        {
            throw new ArgumentException("SandboxOptions.Image must not be blank.", nameof(configure));
        }

        // Throws on a '..' entry; an empty set would leave .git/ and the CI files writable in the sandbox.
        if (new ProtectedPathSet(options.ProtectedPaths).Entries.Count == 0)
        {
            throw new ArgumentException("SandboxOptions.ProtectedPaths must not be empty.", nameof(configure));
        }

        var publish = new GitWorkspaceOptions { DataRoot = Path.Combine(Path.GetFullPath(options.DataRoot), "publish") };
        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(sp => new GitMirrorStore(publish, sp.GetRequiredService<ILogger<GitMirrorStore>>(), sp.GetService<IGitCredentialSource>()));
        services.AddSingleton(sp => new GitPatchApplier(publish, sp.GetRequiredService<ILogger<GitPatchApplier>>()));
        services.AddSingleton(sp => new SandboxPublishWorktrees(new GitWorktreeWorkspaceProvider(
            publish,
            [],
            sp.GetRequiredService<ILogger<GitWorktreeWorkspaceProvider>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IGitCredentialSource>())));
        services.AddHttpClient<SandboxControlClient>();
        services.AddSingleton(sp => new SandboxRunWorkspaceProvider(
            options,
            sp.GetRequiredService<ISandboxRuntime>(),
            sp.GetRequiredService<GitMirrorStore>(),
            sp.GetRequiredService<SandboxPublishWorktrees>(),
            sp.GetRequiredService<GitPatchApplier>(),
            sp.GetServices<IRunWorkspaceObserver>(),
            sp.GetRequiredService<SandboxControlClient>(),
            sp.GetRequiredService<ILogger<SandboxRunWorkspaceProvider>>(),
            sp.GetRequiredService<TimeProvider>()));

        services.Replace(ServiceDescriptor.Singleton<IRunWorkspaceProvider>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IRunBaseFileReader>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IRunToolEndpointResolver>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IParkableRunWorkspaceProvider>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IRunWorkspaceHandoff>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));

        // AddMcpServer registers the host registry with TryAdd, so this wins in either order.
        services.Replace(ServiceDescriptor.Singleton<IRunToolServerReadiness>(sp => sp.GetRequiredService<SandboxRunWorkspaceProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SandboxReconcileService>());

        builder.AddRemoteRunTools(RunWorkspaceToolOptions.SourceName, typeof(WorkspaceTools));
        builder.AddRemoteRunTools(SandboxToolOptions.SourceName, typeof(SandboxTools));
        return builder;
    }
}
