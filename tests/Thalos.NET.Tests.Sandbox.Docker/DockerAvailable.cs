using Docker.DotNet;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>
/// Whether a local Linux-container Docker engine is there to test against. Tests that need one skip without it: the
/// images under test are Linux-only, so a Windows-container engine, such as a GitHub windows runner's, cannot run them.
/// </summary>
internal static class DockerAvailable
{
    private static readonly Lazy<bool> Probe = new(ProbeEngine);

    /// <summary>The default engine endpoint exists and the engine runs Linux containers.</summary>
    public static bool Value => Probe.Value;

    /// <summary>The reason to skip, for messages: the engine is missing or is not a Linux container engine.</summary>
    public const string SkipReason = "no Docker engine running Linux containers";

    private static bool ProbeEngine()
    {
        var endpoint = OperatingSystem.IsWindows() ? @"\\.\pipe\docker_engine" : "/var/run/docker.sock";
        if (!File.Exists(endpoint))
        {
            return false;
        }

        try
        {
            using var client = new DockerClientBuilder().WithTimeout(TimeSpan.FromSeconds(10)).Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = client.System.GetSystemInfoAsync(cts.Token).GetAwaiter().GetResult();
            return string.Equals(info.OSType, "linux", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
