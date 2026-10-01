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

    /// <summary>Red: join with ',' instead of ';'.</summary>
    [Fact]
    public void An_extension_list_is_semicolon_joined() =>
        Spec(new HashSet<string>(StringComparer.Ordinal) { ".csproj", ".cs" }).Environment(new Uri("http://e:1"))[SandboxEnvironment.WriteExtensions].Should().Be(".cs;.csproj");

    /// <summary>Red: drop the Distinct from the extension projection.</summary>
    [Fact]
    public void Extensions_are_deduplicated_ignoring_case_and_ordered()
    {
        var ext = new HashSet<string>(StringComparer.Ordinal) { ".cs", ".CS", ".csproj" };
        Spec(ext).Environment(new Uri("http://e:1"))[SandboxEnvironment.WriteExtensions].Should().BeOneOf(".cs;.csproj", ".CS;.csproj");
    }

    /// <summary>Red: change the ProtectedPaths separator, use "N" for RunId, or alter any fixed value.</summary>
    [Fact]
    public void Every_value_is_pinned()
    {
        var env = Spec(new HashSet<string>(StringComparer.Ordinal) { ".cs" }).Environment(new Uri("http://egress:3128"));
        env[SandboxEnvironment.RunId].Should().Be("0f8fad5b-d9cb-469f-a165-70867728950e");
        env[SandboxEnvironment.Token].Should().Be("tok");
        env[SandboxEnvironment.WriteExtensions].Should().Be(".cs");
        env[SandboxEnvironment.ProtectedPaths].Should().Be(".github/;AGENT.md");
        env["HTTPS_PROXY"].Should().Be("http://egress:3128/");
        env["HTTP_PROXY"].Should().Be("http://egress:3128/");
        env["NO_PROXY"].Should().Be("localhost,127.0.0.1");
        env["DOTNET_CLI_TELEMETRY_OPTOUT"].Should().Be("1");
        env["DOTNET_NOLOGO"].Should().Be("1");
        env["NUGET_PACKAGES"].Should().Be("/work/nuget");
        env["HOME"].Should().Be("/work/home");
    }

    /// <summary>Red: remove the matching Validate check, one per case.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".a;.b")]
    [InlineData("*")]
    public void A_bad_extension_is_refused(string bad)
    {
        var result = Spec(new HashSet<string>(StringComparer.Ordinal) { bad }).Validate();
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    /// <summary>Red: remove the protected-path check from Validate.</summary>
    [Fact]
    public void A_protected_path_with_a_semicolon_is_refused()
    {
        var spec = Spec() with { ProtectedPaths = new ProtectedPathSet(["a;b"]) };
        spec.Validate().IsFailure.Should().BeTrue();
    }

    /// <summary>Red: make Validate fail unconditionally.</summary>
    [Fact]
    public void A_good_spec_validates()
    {
        Spec().Validate().IsSuccess.Should().BeTrue();
        Spec(new HashSet<string>(StringComparer.Ordinal) { ".cs" }).Validate().IsSuccess.Should().BeTrue();
    }

    /// <summary>Gateway route and container name depend on it. Red: use "D".</summary>
    [Fact]
    public void The_sandbox_id_is_32_lowercase_hex() => Spec().SandboxId.Should().MatchRegex("^[0-9a-f]{32}$");
}
