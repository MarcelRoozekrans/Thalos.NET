using Microsoft.Extensions.DependencyInjection;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

public sealed class GitWorkspaceThalosBuilderExtensionsTests
{
    [Fact]
    public void UseGitWorktreeWorkspaces_registers_the_provider_as_a_singleton()
    {
        var dataRoot = Directory.CreateTempSubdirectory("thalos-git-di-").FullName;
        try
        {
            var services = new ServiceCollection().AddLogging();
            services.AddThalos(t => t.UseGitWorktreeWorkspaces(o => o.DataRoot = dataRoot));
            using var sp = services.BuildServiceProvider();

            // Resolved twice: a same-type check alone stays green even if the registration's lifetime were
            // changed from Singleton to Transient (fix round 2 minor) — only resolving twice and comparing
            // instances actually falsifies "singleton".
            var first = sp.GetRequiredService<IRunWorkspaceProvider>();
            first.Should().BeOfType<GitWorktreeWorkspaceProvider>();
            sp.GetRequiredService<IRunWorkspaceProvider>().Should().BeSameAs(first);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void UseGitWorktreeWorkspaces_registers_IRunWorkspaceGit_as_a_singleton()
    {
        var dataRoot = Directory.CreateTempSubdirectory("thalos-git-di-").FullName;
        try
        {
            var services = new ServiceCollection().AddLogging();
            services.AddThalos(t => t.UseGitWorktreeWorkspaces(o => o.DataRoot = dataRoot));
            using var sp = services.BuildServiceProvider();

            var first = sp.GetRequiredService<IRunWorkspaceGit>();
            first.Should().BeOfType<GitCliRunWorkspaceGit>();
            sp.GetRequiredService<IRunWorkspaceGit>().Should().BeSameAs(first);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void A_relative_DataRoot_is_refused()
    {
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddThalos();

        var act = () => builder.UseGitWorktreeWorkspaces(o => o.DataRoot = "relative/path");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_blank_DataRoot_is_refused()
    {
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddThalos();

        var act = () => builder.UseGitWorktreeWorkspaces(_ => { });

        act.Should().Throw<ArgumentException>();
    }
}
