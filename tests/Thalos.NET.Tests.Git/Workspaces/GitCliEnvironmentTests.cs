using System.Collections;
using Thalos.Git.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>Fix round 1 of A8 (R27): a host can make every git process start from a base environment of its choosing.</summary>
public sealed class GitCliEnvironmentTests : IDisposable
{
    private const string Secret = "host-secret-token-value";
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-gitcli-env-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("THALOS_SANDBOX_TOKEN");

    public GitCliEnvironmentTests() => Environment.SetEnvironmentVariable("THALOS_SANDBOX_TOKEN", Secret);

    /// <summary>Red: in GitCli.BuildStartInfo, ignore GitWorkspaceOptions.BaseEnvironment; git then inherits the token and lacks the marker.</summary>
    [Fact]
    public async Task Git_starts_from_the_base_environment_not_the_hosts()
    {
        var baseEnvironment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(e => !string.Equals((string)e.Key, "THALOS_SANDBOX_TOKEN", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
        baseEnvironment["BASE_MARKER"] = "yes";
        var git = new GitCli(new GitWorkspaceOptions { DataRoot = Path.Combine(_temp, "data"), BaseEnvironment = baseEnvironment });

        var result = await git.RunAsync(_temp, ["dumpenv"], ["alias.dumpenv=!env"], null, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.StdErr);
        result.StdOut.Should().Contain("BASE_MARKER=yes").And.NotContain(Secret);
        result.StdOut.Should().Contain("GIT_CONFIG_NOSYSTEM=1", "GitCli's own isolation still applies over the base");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THALOS_SANDBOX_TOKEN", _previous);
        Directory.Delete(_temp, recursive: true);
    }
}
