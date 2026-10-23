using Microsoft.Extensions.DependencyInjection;
using Thalos.Sandbox;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox;

/// <summary>What the reflection-free <see cref="SandboxToolSourceExtensions.AddRemoteRunTools{TTools}"/> refuses at registration.</summary>
public sealed class RemoteRunToolsOfTTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = RunWorkspaceToolOptions.SerializerOptions;

    [Fact]
    public void AddRemoteRunTools_of_T_refuses_an_unmarked_type()
    {
        var unmarked = () => new ServiceCollection().AddThalos(t => t.AddRemoteRunTools<RunWorkspaceToolOptions>("workspace", Json, o => o.ToString));

        // Red: drop the LocalToolSources.ThrowIfInvalid call from AddRemoteRunTools<TTools>: the type is accepted.
        unmarked.Should().Throw<ArgumentException>().WithMessage("*is not marked [ThalosToolType]*").WithParameterName("tools");
    }

    [Fact]
    public void AddRemoteRunTools_of_T_refuses_no_binder()
    {
        var none = () => new ServiceCollection().AddThalos(t => t.AddRemoteRunTools<WorkspaceTools>("workspace", Json));

        // Red: drop the LocalToolSources.ThrowIfInvalid call from AddRemoteRunTools<TTools>: an empty list is accepted.
        none.Should().Throw<ArgumentException>().WithMessage("At least one tool binder is required.*").WithParameterName("tools");
    }

    [Fact]
    public void AddRemoteRunTools_of_T_refuses_an_invalid_name()
    {
        var invalid = () => new ServiceCollection().AddThalos(t => t.AddRemoteRunTools<WorkspaceTools>("work__space", Json, w => w.ReadFile));

        // Red: drop the ToolSourceName check from AddRemoteRunTools<TTools>: the name is accepted.
        invalid.Should().Throw<ArgumentException>().WithMessage("Tool source name 'work__space' is invalid*").WithParameterName("sourceName");
    }

    [Fact]
    public void AddRemoteRunTools_of_T_refuses_a_taken_name()
    {
        var twice = () => new ServiceCollection().AddThalos(t => t
            .AddRemoteRunTools<WorkspaceTools>("workspace", Json, w => w.ReadFile)
            .AddRemoteRunTools<WorkspaceTools>("workspace", Json, w => w.ReadFile));

        // Red: in AddRemote, register with Services.AddSingleton instead of McpThalosBuilderExtensions.AddRemote: the
        // second source is accepted.
        twice.Should().Throw<ArgumentException>().WithMessage("A remote run tool source named 'workspace' was already added.*");
    }
}
