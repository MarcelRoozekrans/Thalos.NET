using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Thalos.Git.LibGit2Sharp;

/// <summary>Registers the LibGit2Sharp-backed <see cref="IGitWriteService"/> on a <see cref="ThalosBuilder"/>.</summary>
public static class GitLibGit2SharpThalosBuilderExtensions
{
    /// <summary>Uses <see cref="LibGit2SharpGitWriteService"/> as the <see cref="IGitWriteService"/>, replacing any earlier registration.</summary>
    public static ThalosBuilder UseLibGit2SharpGit(this ThalosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IGitWriteService, LibGit2SharpGitWriteService>());
        return builder;
    }
}
