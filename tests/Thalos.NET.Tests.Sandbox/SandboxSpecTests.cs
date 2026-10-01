using Thalos.Sandbox;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox;

public sealed class SandboxSpecTests
{
    private static SandboxSpec Spec(IReadOnlySet<string>? ext = null) => new()
    {
        RunId = new Guid(0x0f8fad5b, 0xd9cb, 0x469f, 0xa1, 0x65, 0x70, 0x86, 0x77, 0x28, 0x95, 0x0e),
        Image = "img:1",
        Token = "tok",
        AllowedWriteExtensions = ext,
        ProtectedPaths = new ProtectedPathSet([".github/", "AGENT.md"]),
        Limits = new(),
    };

    /// <summary>S1. Red: copy any variable from Environment.GetEnvironmentVariables() into the dictionary.</summary>
    [Fact]
    public void The_environment_is_exactly_the_listed_keys()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "should-not-appear");
        try
        {
            Spec().Environment(new Uri("http://egress:3128")).Keys.Should().BeEquivalentTo(
                SandboxEnvironment.RunId, SandboxEnvironment.Token, SandboxEnvironment.WriteExtensions, SandboxEnvironment.ProtectedPaths,
                "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "NUGET_PACKAGES", "HOME");
        }
        finally { Environment.SetEnvironmentVariable("GITHUB_TOKEN", null); }
    }

    /// <summary>Red: serialise null as "", which the host reads as "nothing writable".</summary>
    [Fact]
    public void Any_extension_is_written_as_a_star() =>
        Spec().Environment(new Uri("http://e:1"))[SandboxEnvironment.WriteExtensions].Should().Be("*");

    /// <summary>Red: drop the leading dot from each extension when joining.</summary>
    [Fact]
    public void An_extension_list_is_semicolon_joined() =>
        Spec(new HashSet<string>(StringComparer.Ordinal) { ".cs" }).Environment(new Uri("http://e:1"))[SandboxEnvironment.WriteExtensions].Should().Be(".cs");

    /// <summary>Gateway route and container name depend on it. Red: use "D".</summary>
    [Fact]
    public void The_sandbox_id_is_32_lowercase_hex() => Spec().SandboxId.Should().MatchRegex("^[0-9a-f]{32}$");
}
