using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>Registers <see cref="GitWorktreeWorkspaceProvider"/> on a <see cref="ThalosBuilder"/>.</summary>
public static class GitWorkspaceThalosBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="GitWorktreeWorkspaceProvider"/> as the (singleton) <see cref="IRunWorkspaceProvider"/>,
    /// replacing any earlier registration, and registers <see cref="TimeProvider.System"/> if nothing else already
    /// has (<see cref="GitWorktreeWorkspaceProvider"/>'s own <c>clock</c> parameter is required — ruling R9).
    /// </summary>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="configure">Sets <see cref="GitWorkspaceOptions"/>, in particular the required <see cref="GitWorkspaceOptions.DataRoot"/>.</param>
    /// <exception cref="ArgumentException"><see cref="GitWorkspaceOptions.DataRoot"/> is blank or not an absolute path once <paramref name="configure"/> has run.</exception>
    public static ThalosBuilder UseGitWorktreeWorkspaces(this ThalosBuilder builder, Action<GitWorkspaceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new GitWorkspaceOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.DataRoot) || !Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException($"GitWorkspaceOptions.DataRoot must be an absolute path (was '{options.DataRoot}').", nameof(configure));
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.Replace(ServiceDescriptor.Singleton<IRunWorkspaceProvider>(sp => new GitWorktreeWorkspaceProvider(
            options,
            sp.GetServices<IRunWorkspaceObserver>(),
            sp.GetRequiredService<ILogger<GitWorktreeWorkspaceProvider>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IGitCredentialSource>())));

        return builder;
    }
}
