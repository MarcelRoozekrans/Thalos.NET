namespace Thalos.Sandbox;

/// <summary>The observed state of a sandbox.</summary>
public enum SandboxState
{
    /// <summary>The container is running.</summary>
    Running,

    /// <summary>The container has stopped.</summary>
    Exited,

    /// <summary>The container no longer exists.</summary>
    Missing,
}

/// <summary>A handle to a created sandbox.</summary>
/// <param name="SandboxId">The run id in "N" format: container-name suffix and gateway route.</param>
/// <param name="RunId">The workflow run the sandbox belongs to.</param>
/// <param name="State">The observed container state.</param>
/// <param name="BaseAddress">The address the host reaches the sandbox on.</param>
/// <param name="CreatedAt">When the sandbox was created.</param>
/// <param name="ExitCode">The exit code once exited, otherwise null.</param>
/// <param name="OomKilled">Whether the container was killed for exceeding its memory limit.</param>
public sealed record SandboxHandle(string SandboxId, Guid RunId, SandboxState State, Uri BaseAddress, DateTimeOffset CreatedAt, int? ExitCode = null, bool OomKilled = false);

/// <summary>Resource limits applied to one sandbox.</summary>
/// <param name="Cpus">CPU quota.</param>
/// <param name="MemoryBytes">Memory limit in bytes.</param>
/// <param name="PidsLimit">Maximum process count.</param>
/// <param name="TmpfsBytes">Size of the tmpfs mount in bytes.</param>
public sealed record SandboxLimits(double Cpus = 2, long MemoryBytes = 4L * 1024 * 1024 * 1024, long PidsLimit = 512, long TmpfsBytes = 512L * 1024 * 1024);
