using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Docker;

/// <summary>
/// Runs each sandbox as a hardened Docker container on an internal network, reachable from the host only through the
/// loopback gateway and able to reach out only through the NuGet egress proxy.
/// </summary>
/// <remarks>
/// <para>
/// <b>S1.</b> A run container's environment is exactly <see cref="SandboxSpec.Environment"/>; nothing of the host's
/// environment is passed. <b>S2.</b> It is attached to the internal network only, which has no route out; the egress
/// proxy on that network allows <c>*.nuget.org</c> and the reviewed <see cref="DockerSandboxOptions.ExtraEgressDomains"/>.
/// </para>
/// <para>
/// Every failure of the engine, including an unreachable one, is returned as an <see cref="AgentError"/>. A run
/// container's rootfs is read-only, so anything put into it later goes to the <c>/work</c> volume.
/// </para>
/// <para>
/// <see cref="GetAsync"/> and <see cref="ListAsync"/> have no error channel: when the engine fails they log it and
/// return null or an empty list, which callers already treat as "no sandbox", never as a reason to delete one.
/// </para>
/// </remarks>
public sealed partial class DockerSandboxRuntime(DockerSandboxOptions options, TimeProvider clock, ILogger<DockerSandboxRuntime> logger) : ISandboxRuntime, IAsyncDisposable
{
    private const string RunUser = "10001:10001";
    private const string WorkPath = "/work";

    private readonly DockerSandboxInfrastructure infrastructure = new(BuildClient(options), options, clock, logger);

    private DockerClient Docker => infrastructure.Docker;

    /// <inheritdoc />
    public async ValueTask<Result<SandboxHandle, AgentError>> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var valid = spec.Validate();
        if (valid.IsFailure)
        {
            return Result<SandboxHandle, AgentError>.Failure(valid.Error);
        }

        var infra = await infrastructure.EnsureAsync(ct).ConfigureAwait(false);
        if (infra.IsFailure)
        {
            return Fail(spec.SandboxId, infra.Error.Message, infra.Error.Detail);
        }

        var id = spec.SandboxId;
        var started = clock.GetTimestamp();
        var volumeMade = false;
        string? containerId = null;
        try
        {
            if (await DockerErrors.TryInspectVolumeAsync(Docker, VolumeName(id), ct).ConfigureAwait(false) is not null)
            {
                return Fail(id, $"the volume '{VolumeName(id)}' already exists; delete the sandbox first", detail: null);
            }

            await Docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = VolumeName(id), Labels = RunLabels(spec.RunId) }, ct).ConfigureAwait(false);
            volumeMade = true;

            var created = await Docker.Containers.CreateContainerAsync(ContainerParameters(spec, infra.Value.EgressProxy), ct).ConfigureAwait(false);
            containerId = created.ID;
            await Docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);

            var inspect = await Docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            var elapsedMs = (long)clock.GetElapsedTime(started).TotalMilliseconds;
            LogCreated(logger, id, spec.RunId, elapsedMs);
            return Result<SandboxHandle, AgentError>.Success(ToHandle(id, spec.RunId, inspect, infra.Value.GatewayPort));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await CleanUpAsync(id, containerId, volumeMade).ConfigureAwait(false);
            return Fail(id, DockerErrors.Describe(ex, options.EngineTimeout), detail: null);
        }
        catch (OperationCanceledException)
        {
            await CleanUpAsync(id, containerId, volumeMade).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<SandboxHandle?> GetAsync(string sandboxId, CancellationToken ct)
    {
        if (!IsSandboxId(sandboxId))
        {
            return null;
        }

        var infra = await infrastructure.EnsureAsync(ct).ConfigureAwait(false);
        if (infra.IsFailure)
        {
            LogLookupFailed(logger, sandboxId, infra.Error.Message);
            return null;
        }

        try
        {
            var inspect = await DockerErrors.TryInspectContainerAsync(Docker, ContainerName(sandboxId), ct).ConfigureAwait(false);
            return inspect is not null && IsOwnRun(inspect.Config?.Labels, sandboxId, out var runId)
                ? ToHandle(sandboxId, runId, inspect, infra.Value.GatewayPort)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogLookupFailed(logger, sandboxId, DockerErrors.Describe(ex, options.EngineTimeout));
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SandboxHandle>> ListAsync(CancellationToken ct)
    {
        var infra = await infrastructure.EnsureAsync(ct).ConfigureAwait(false);
        if (infra.IsFailure)
        {
            LogListFailed(logger, infra.Error.Message);
            return [];
        }

        try
        {
            var containers = await Docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = DockerErrors.Labels(
                        (SandboxLabels.Sandbox, "true"),
                        (SandboxLabels.Role, SandboxLabels.RoleRun),
                        (SandboxLabels.Network, options.InternalNetwork)),
                },
                ct).ConfigureAwait(false);

            var handles = new List<SandboxHandle>(containers.Count);
            foreach (var container in containers)
            {
                if (container.Labels is null
                    || !container.Labels.TryGetValue(SandboxLabels.RunId, out var label)
                    || !Guid.TryParseExact(label, "D", out var labelled))
                {
                    continue;
                }

                var id = labelled.ToString("N");
                var inspect = await DockerErrors.TryInspectContainerAsync(Docker, container.ID, ct).ConfigureAwait(false);
                if (inspect is not null
                    && string.Equals(inspect.Name, "/" + ContainerName(id), StringComparison.Ordinal)
                    && IsOwnRun(inspect.Config?.Labels, id, out var runId))
                {
                    handles.Add(ToHandle(id, runId, inspect, infra.Value.GatewayPort));
                }
            }

            return handles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogListFailed(logger, DockerErrors.Describe(ex, options.EngineTimeout));
            return [];
        }
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeleteAsync(string sandboxId, CancellationToken ct)
    {
        if (!IsSandboxId(sandboxId))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation($"'{sandboxId}' is not a sandbox id."));
        }

        try
        {
            var container = await DockerErrors.TryInspectContainerAsync(Docker, ContainerName(sandboxId), ct).ConfigureAwait(false);
            if (container is not null)
            {
                if (!IsOwnRun(container.Config?.Labels, sandboxId, out _))
                {
                    return UnitResult<AgentError>.Failure(AgentError.Validation($"container '{ContainerName(sandboxId)}' is not a sandbox of network '{options.InternalNetwork}'; refusing to delete it"));
                }

                await RemoveContainerAsync(container.ID, ct).ConfigureAwait(false);
            }

            var volume = await DockerErrors.TryInspectVolumeAsync(Docker, VolumeName(sandboxId), ct).ConfigureAwait(false);
            if (volume is not null)
            {
                if (!IsOwnRun(volume.Labels, sandboxId, out _))
                {
                    return UnitResult<AgentError>.Failure(AgentError.Validation($"volume '{VolumeName(sandboxId)}' is not a sandbox volume of network '{options.InternalNetwork}'; refusing to delete it"));
                }

                await RemoveVolumeAsync(volume.Name, ct).ConfigureAwait(false);
            }

            LogDeleted(logger, sandboxId);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = DockerErrors.Describe(ex, options.EngineTimeout);
            LogDeleteFailed(logger, sandboxId, error);
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not delete the sandbox '{sandboxId}': {error}"));
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        infrastructure.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The run container's name; the gateway resolves it by this name.</summary>
    internal static string ContainerName(string sandboxId) => $"thalos-sandbox-{sandboxId}";

    /// <summary>The run's <c>/work</c> volume.</summary>
    internal static string VolumeName(string sandboxId) => $"thalos-sandbox-{sandboxId}-work";

    /// <summary>The create parameters of a run container: exactly these, and nothing from the host.</summary>
    internal CreateContainerParameters ContainerParameters(SandboxSpec spec, Uri egressProxy)
    {
        var limits = spec.Limits;
        return new CreateContainerParameters
        {
            Image = spec.Image,
            Name = ContainerName(spec.SandboxId),
            Labels = RunLabels(spec.RunId),
            User = RunUser,
            Env = [.. spec.Environment(egressProxy).Select(pair => $"{pair.Key}={pair.Value}")],
            HostConfig = new HostConfig
            {
                NetworkMode = options.InternalNetwork,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges:true"],
                ReadonlyRootfs = true,
                NanoCPUs = (long)Math.Round(limits.Cpus * 1_000_000_000d),
                Memory = limits.MemoryBytes,
                MemorySwap = limits.MemoryBytes,
                PidsLimit = limits.PidsLimit,
                Tmpfs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/tmp"] = $"rw,noexec,nosuid,size={limits.TmpfsBytes.ToString(CultureInfo.InvariantCulture)}",
                },
                Mounts = [new Mount { Type = "volume", Source = VolumeName(spec.SandboxId), Target = WorkPath }],
                AutoRemove = false,
            },
        };
    }

    private static DockerClient BuildClient(DockerSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var endpoint = options.Endpoint ?? (OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock"));
        return new DockerClientBuilder().WithEndpoint(endpoint).WithTimeout(options.EngineTimeout).Build();
    }

    private static bool IsSandboxId(string? sandboxId) =>
        sandboxId is { Length: 32 } && sandboxId.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static SandboxHandle ToHandle(string sandboxId, Guid runId, ContainerInspectResponse inspect, int gatewayPort)
    {
        var running = inspect.State?.Running == true;
        var created = inspect.Created.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(inspect.Created, DateTimeKind.Utc) : inspect.Created.ToUniversalTime();
        return new SandboxHandle(
            sandboxId,
            runId,
            running ? SandboxState.Running : SandboxState.Exited,
            new Uri($"http://127.0.0.1:{gatewayPort.ToString(CultureInfo.InvariantCulture)}/sandboxes/{sandboxId}/"),
            new DateTimeOffset(created, TimeSpan.Zero),
            running ? null : (int?)inspect.State?.ExitCode,
            inspect.State?.OOMKilled == true);
    }

    private Dictionary<string, string> RunLabels(Guid runId) => new(StringComparer.Ordinal)
    {
        [SandboxLabels.Sandbox] = "true",
        [SandboxLabels.Role] = SandboxLabels.RoleRun,
        [SandboxLabels.RunId] = runId.ToString("D"),
        [SandboxLabels.Network] = options.InternalNetwork,
    };

    /// <summary>Whether the labels mark a run of this runtime's network with this sandbox id.</summary>
    private bool IsOwnRun(IDictionary<string, string>? labels, string sandboxId, out Guid runId)
    {
        runId = Guid.Empty;
        return labels is not null
            && labels.TryGetValue(SandboxLabels.Sandbox, out var sandbox) && string.Equals(sandbox, "true", StringComparison.Ordinal)
            && labels.TryGetValue(SandboxLabels.Role, out var role) && string.Equals(role, SandboxLabels.RoleRun, StringComparison.Ordinal)
            && labels.TryGetValue(SandboxLabels.Network, out var network) && string.Equals(network, options.InternalNetwork, StringComparison.Ordinal)
            && labels.TryGetValue(SandboxLabels.RunId, out var label) && Guid.TryParseExact(label, "D", out runId)
            && string.Equals(runId.ToString("N"), sandboxId, StringComparison.Ordinal);
    }

    private async ValueTask RemoveContainerAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await Docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (DockerErrors.IsNotFound(ex))
        {
            // Already gone.
        }
    }

    private async ValueTask RemoveVolumeAsync(string name, CancellationToken ct)
    {
        try
        {
            await Docker.Volumes.RemoveAsync(name, force: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (DockerErrors.IsNotFound(ex))
        {
            // Already gone.
        }
    }

    /// <summary>Best effort: removes what a failed create made, and nothing else.</summary>
    private async ValueTask CleanUpAsync(string sandboxId, string? containerId, bool volumeMade)
    {
        if (containerId is not null)
        {
            try
            {
                await RemoveContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCleanupFailed(logger, sandboxId, "remove the container", DockerErrors.Describe(ex, options.EngineTimeout));
            }
        }

        if (volumeMade)
        {
            try
            {
                await RemoveVolumeAsync(VolumeName(sandboxId), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCleanupFailed(logger, sandboxId, "remove the volume", DockerErrors.Describe(ex, options.EngineTimeout));
            }
        }
    }

    private Result<SandboxHandle, AgentError> Fail(string sandboxId, string reason, string? detail)
    {
        LogCreateFailed(logger, sandboxId, reason);
        return Result<SandboxHandle, AgentError>.Failure(AgentError.ProviderError($"could not create the run's sandbox: {reason}", detail));
    }

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information, Message = "Created sandbox {SandboxId} for run {RunId} in {ElapsedMs} ms")]
    private static partial void LogCreated(ILogger logger, string sandboxId, Guid runId, long elapsedMs);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Warning, Message = "Could not create sandbox {SandboxId}: {Reason}")]
    private static partial void LogCreateFailed(ILogger logger, string sandboxId, string reason);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Warning, Message = "Could not {What} of the failed sandbox {SandboxId}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string sandboxId, string what, string error);

    [LoggerMessage(EventId = 1213, Level = LogLevel.Information, Message = "Deleted sandbox {SandboxId}")]
    private static partial void LogDeleted(ILogger logger, string sandboxId);

    [LoggerMessage(EventId = 1214, Level = LogLevel.Warning, Message = "Could not delete sandbox {SandboxId}: {Error}")]
    private static partial void LogDeleteFailed(ILogger logger, string sandboxId, string error);

    [LoggerMessage(EventId = 1215, Level = LogLevel.Warning, Message = "Could not look up sandbox {SandboxId}; treating it as absent: {Error}")]
    private static partial void LogLookupFailed(ILogger logger, string sandboxId, string error);

    [LoggerMessage(EventId = 1216, Level = LogLevel.Warning, Message = "Could not list sandboxes; returning none: {Error}")]
    private static partial void LogListFailed(ILogger logger, string error);
}
