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

            sp.GetRequiredService<IRunWorkspaceProvider>().Should().BeOfType<GitWorktreeWorkspaceProvider>();
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

            sp.GetRequiredService<IRunWorkspaceGit>().Should().BeOfType<GitCliRunWorkspaceGit>();
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
