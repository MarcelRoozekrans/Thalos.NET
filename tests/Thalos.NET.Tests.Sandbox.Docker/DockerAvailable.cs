namespace Thalos.Tests.Sandbox.Docker;

/// <summary>Whether a local Docker engine is there to test against. Tests that need one skip without it.</summary>
internal static class DockerAvailable
{
    private static readonly Lazy<bool> Probe = new(() => OperatingSystem.IsWindows()
        ? File.Exists(@"\\.\pipe\docker_engine")
        : File.Exists("/var/run/docker.sock"));

    /// <summary>The default engine endpoint exists: the named pipe on Windows, the socket elsewhere.</summary>
    public static bool Value => Probe.Value;
}
