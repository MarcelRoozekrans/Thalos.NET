using System.Collections;
using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

/// <summary>Fix round 1 of A8 (R27): every sandbox child starts from an explicit environment, never the host's.</summary>
public sealed class SandboxChildEnvironmentTests : IDisposable
{
    private const string Secret = "host-secret-token-value";
    private readonly string? _previous = Environment.GetEnvironmentVariable(SandboxEnvironment.Token);

    public SandboxChildEnvironmentTests() => Environment.SetEnvironmentVariable(SandboxEnvironment.Token, Secret);

    /// <summary>Red: in SandboxProcessRunner.Start, skip info.Environment.Clear(); the child then inherits the token.</summary>
    [Fact]
    public async Task A_spec_environment_is_the_childs_whole_environment()
    {
        var environment = new Dictionary<string, string>(SandboxChildEnvironment.Curated(), StringComparer.Ordinal) { ["SPEC_MARKER"] = "yes" };

        var output = await EnvOfChildAsync(environment);

        output.Should().Contain("SPEC_MARKER=yes").And.NotContain(Secret);
    }

    /// <summary>Red: in SandboxProcessRunner.Start, set the environment only when the spec carries one; a null spec then inherits the token.</summary>
    [Fact]
    public async Task A_spec_without_an_environment_gets_the_curated_one()
    {
        var output = await EnvOfChildAsync(environment: null);

        output.Should().NotContain(Secret).And.Contain("PATH=", "the curated essentials are there");
    }

    /// <summary>Red: in SandboxChildEnvironment.Build, copy every host variable instead of the listed names and prefixes.</summary>
    [Fact]
    public void The_curated_environment_keeps_only_dotnet_nuget_proxy_and_os_essentials()
    {
        var host = new Hashtable
        {
            [SandboxEnvironment.Token] = Secret,
            ["GITHUB_TOKEN"] = "gh",
            ["DOTNET_CLI_HOME"] = "/work/home",
            ["NUGET_PACKAGES"] = "/work/nuget",
            ["HTTPS_PROXY"] = "http://egress:3128",
            ["TMPDIR"] = "/tmp",
        };

        var curated = SandboxChildEnvironment.Build(() => host);

        curated.Should().Contain("DOTNET_CLI_HOME", "/work/home").And.Contain("NUGET_PACKAGES", "/work/nuget")
            .And.Contain("HTTPS_PROXY", "http://egress:3128").And.Contain("TMPDIR", "/tmp");
        curated.Keys.Should().NotContain(SandboxEnvironment.Token).And.NotContain("GITHUB_TOKEN");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(SandboxEnvironment.Token, _previous);

    private static async Task<string> EnvOfChildAsync(IReadOnlyDictionary<string, string>? environment)
    {
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", "set"], Path.GetTempPath(), TimeSpan.FromSeconds(30), Environment: environment)
            : new ProcessSpec("sh", ["-c", "env"], Path.GetTempPath(), TimeSpan.FromSeconds(30), Environment: environment);
        var outcome = await new SandboxProcessRunner().RunAsync(spec, CancellationToken.None);
        outcome.ExitCode.Should().Be(0, outcome.FullOutput);
        return outcome.FullOutput;
    }
}
