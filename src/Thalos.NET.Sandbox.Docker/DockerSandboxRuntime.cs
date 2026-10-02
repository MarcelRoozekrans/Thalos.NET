using System.Globalization;
using System.Net;
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
/// return null or an empty list, which callers treat as "cannot tell", never as a reason to delete one. Only the
/// engine's own 404 for the run's container is a verdict: <see cref="GetAsync"/> then answers
/// <see cref="SandboxState.Missing"/>.
/// </para>
/// </remarks>
public sealed partial class DockerSandboxRuntime(DockerSandboxOptions options, TimeProvider clock, ILogger<DockerSandboxRuntime> logger) : ISandboxRuntime, IAsyncDisposable
{
    private const string RunUser = "10001:10001";
    private const string WorkPath = "/work";

    private readonly DockerSandboxInfrastructure infrastructure = new(BuildClient(options), options, clock, logger);
    private readonly KeyedLock perSandbox = new();

    private DockerClient Docker => infrastructure.Docker;

    /// <summary>Test seam, null in production: awaited when a create reaches a named stage, so a test can interleave two creates.</summary>
    internal Func<string, CancellationToken, Task>? StageReached { get; set; }

    /// <inheritdoc />
    public async ValueTask<Result<SandboxHandle, AgentError>> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var valid = spec.Validate();
        if (valid.IsFailure)
        {
            return Result<SandboxHandle, AgentError>.Failure(valid.Error);
        }

        // One create or delete per sandbox id at a time in this process, so a create's clean-up never removes what a
        // concurrent create of the same run made.
        using var held = await perSandbox.TryAcquireAsync(spec.SandboxId, LockTimeout, ct).ConfigureAwait(false);
        if (held is null)
        {
            return Fail(spec.SandboxId, "another create or delete of this sandbox is still in progress", detail: null);
        }

        // At most two attempts: a create that fails because the network or an infrastructure container went away
        // between the check and the create sets the infrastructure up again and tries once more.
        for (var attempt = 1; ; attempt++)
        {
            var infra = await infrastructure.EnsureAsync(ct).ConfigureAwait(false);
            if (infra.IsFailure)
            {
                return Fail(spec.SandboxId, infra.Error.Message, infra.Error.Detail);
            }

            var result = await CreateOnceAsync(spec, infra.Value, ct).ConfigureAwait(false);
            if (result.IsSuccess || attempt == 2 || !await infrastructure.RecheckAsync(infra.Value, ct).ConfigureAwait(false))
            {
                return result;
            }
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
            if (inspect is null)
            {
                // The engine answered 404: the run's container is gone. Its run is the one the id names.
                return Missing(sandboxId, infra.Value.GatewayPort);
            }

            // A container of this name that is not this runtime's is not a verdict on the run's own.
            return IsOwnRun(inspect.Config?.Labels, sandboxId, out var runId)
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
    /// <remarks>
    /// Serialised with creates and deletes of the same sandbox, and refuses a container that does not carry this
    /// runtime's labels for the run, like <see cref="DeleteAsync"/>. Each engine call is bounded by
    /// <see cref="DockerSandboxOptions.EngineTimeout"/>.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> StartAsync(string sandboxId, CancellationToken ct)
    {
        if (!IsSandboxId(sandboxId))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation($"'{sandboxId}' is not a sandbox id."));
        }

        using var held = await perSandbox.TryAcquireAsync(sandboxId, LockTimeout, ct).ConfigureAwait(false);
        if (held is null)
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not start the sandbox '{sandboxId}': another create or delete of it is still in progress"));
        }

        try
        {
            var container = await DockerErrors.TryInspectContainerAsync(Docker, ContainerName(sandboxId), ct).ConfigureAwait(false);
            if (container is null)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not start the sandbox '{sandboxId}': it does not exist"));
            }

            if (!IsOwnRun(container.Config?.Labels, sandboxId, out _))
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation($"container '{ContainerName(sandboxId)}' is not a sandbox of network '{options.InternalNetwork}'; refusing to start it"));
            }

            if (container.State?.Running != true)
            {
                await Docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
                LogStarted(logger, sandboxId);
            }

            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = DockerErrors.Describe(ex, options.EngineTimeout);
            LogStartFailed(logger, sandboxId, error);
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not start the sandbox '{sandboxId}': {error}"));
        }
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeleteAsync(string sandboxId, CancellationToken ct)
    {
        if (!IsSandboxId(sandboxId))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation($"'{sandboxId}' is not a sandbox id."));
        }

        using var held = await perSandbox.TryAcquireAsync(sandboxId, LockTimeout, ct).ConfigureAwait(false);
        if (held is null)
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not delete the sandbox '{sandboxId}': another create or delete of it is still in progress"));
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
    internal CreateContainerParameters ContainerParameters(SandboxSpec spec, SandboxInfrastructureState infra)
    {
        var limits = spec.Limits;
        return new CreateContainerParameters
        {
            Image = spec.Image,
            Name = ContainerName(spec.SandboxId),
            Labels = RunLabels(spec.RunId),
            User = RunUser,
            Env = [.. spec.Environment(infra.EgressProxy).Select(pair => $"{pair.Key}={pair.Value}")],
            HostConfig = new HostConfig
            {
                NetworkMode = infra.NetworkId,
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

    private async ValueTask<Result<SandboxHandle, AgentError>> CreateOnceAsync(SandboxSpec spec, SandboxInfrastructureState infra, CancellationToken ct)
    {
        var id = spec.SandboxId;
        var started = clock.GetTimestamp();
        var volumeAttempted = false;
        var containerAttempted = false;
        try
        {
            if (await DockerErrors.TryInspectContainerAsync(Docker, ContainerName(id), ct).ConfigureAwait(false) is not null)
            {
                return Fail(id, $"the container '{ContainerName(id)}' already exists; delete the sandbox first", detail: null);
            }

            if (await DockerErrors.TryInspectVolumeAsync(Docker, VolumeName(id), ct).ConfigureAwait(false) is { } leftover)
            {
                if (!IsOwnRun(leftover.Labels, id, out _))
                {
                    return Fail(id, $"the volume '{VolumeName(id)}' exists and is not a sandbox volume of network '{options.InternalNetwork}'", detail: null);
                }

                // A leftover of an earlier attempt for this run, with no container: never reuse a stale /work.
                LogReplacingLeftoverVolume(logger, id);
                await RemoveVolumeAsync(VolumeName(id), ct).ConfigureAwait(false);
            }

            await ReachedAsync("prechecked", ct).ConfigureAwait(false);
            volumeAttempted = true;
            await Docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = VolumeName(id), Labels = RunLabels(spec.RunId) }, ct).ConfigureAwait(false);
            await ReachedAsync("volume-created", ct).ConfigureAwait(false);

            CreateContainerResponse created;
            try
            {
                containerAttempted = true;
                created = await Docker.Containers.CreateContainerAsync(ContainerParameters(spec, infra), ct).ConfigureAwait(false);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Another create, outside this process, owns the name and shares the volume name: touch neither.
                return Fail(id, $"another create of this sandbox owns '{ContainerName(id)}'", detail: null);
            }

            await Docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);

            var inspect = await Docker.Containers.InspectContainerAsync(created.ID, ct).ConfigureAwait(false);
            var elapsedMs = (long)clock.GetElapsedTime(started).TotalMilliseconds;
            LogCreated(logger, id, spec.RunId, elapsedMs);
            return Result<SandboxHandle, AgentError>.Success(ToHandle(id, spec.RunId, inspect, infra.GatewayPort));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await CleanUpAsync(id, containerAttempted, volumeAttempted).ConfigureAwait(false);
            return Fail(id, DockerErrors.Describe(ex, options.EngineTimeout), detail: null);
        }
        catch (OperationCanceledException)
        {
            await CleanUpAsync(id, containerAttempted, volumeAttempted).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>How long a create or delete waits for another one of the same sandbox: a whole set-up plus a create.</summary>
    private TimeSpan LockTimeout => infrastructure.SetUpBudget + (10 * options.EngineTimeout);

    private Task ReachedAsync(string stage, CancellationToken ct) => StageReached?.Invoke(stage, ct) ?? Task.CompletedTask;

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

    /// <summary>The handle of a run container the engine reported absent; its run is the one the sandbox id names.</summary>
    private static SandboxHandle Missing(string sandboxId, int gatewayPort) => new(
        sandboxId,
        Guid.ParseExact(sandboxId, "N"),
        SandboxState.Missing,
        new Uri($"http://127.0.0.1:{gatewayPort.ToString(CultureInfo.InvariantCulture)}/sandboxes/{sandboxId}/"),
        DateTimeOffset.UnixEpoch);

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
            await Docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Best effort, by name: removes the run's container and volume when they carry this runtime's labels for this run.
    /// By name, not by id, because a client timeout can leave an object the engine created but whose id never came back.
    /// </summary>
    private async ValueTask CleanUpAsync(string sandboxId, bool containerAttempted, bool volumeAttempted)
    {
        if (containerAttempted)
        {
            await CleanUpContainerAsync(sandboxId).ConfigureAwait(false);
        }

        if (volumeAttempted)
        {
            await CleanUpVolumeAsync(sandboxId).ConfigureAwait(false);
        }
    }

    private async ValueTask CleanUpContainerAsync(string sandboxId)
    {
        try
        {
            var container = await DockerErrors.TryInspectContainerAsync(Docker, ContainerName(sandboxId), CancellationToken.None).ConfigureAwait(false);
            if (container is not null && IsOwnRun(container.Config?.Labels, sandboxId, out _))
            {
                await RemoveContainerAsync(container.ID, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogCleanupFailed(logger, sandboxId, "remove the container", DockerErrors.Describe(ex, options.EngineTimeout));
        }
    }

    private async ValueTask CleanUpVolumeAsync(string sandboxId)
    {
        try
        {
            var volume = await DockerErrors.TryInspectVolumeAsync(Docker, VolumeName(sandboxId), CancellationToken.None).ConfigureAwait(false);
            if (volume is not null && IsOwnRun(volume.Labels, sandboxId, out _))
            {
                await RemoveVolumeAsync(volume.Name, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogCleanupFailed(logger, sandboxId, "remove the volume", DockerErrors.Describe(ex, options.EngineTimeout));
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

    [LoggerMessage(EventId = 1217, Level = LogLevel.Warning, Message = "Replacing the leftover work volume of sandbox {SandboxId}, which has no container")]
    private static partial void LogReplacingLeftoverVolume(ILogger logger, string sandboxId);

    [LoggerMessage(EventId = 1218, Level = LogLevel.Information, Message = "Started sandbox {SandboxId} again")]
    private static partial void LogStarted(ILogger logger, string sandboxId);

    [LoggerMessage(EventId = 1219, Level = LogLevel.Warning, Message = "Could not start sandbox {SandboxId}: {Error}")]
    private static partial void LogStartFailed(ILogger logger, string sandboxId, string error);

    [LoggerMessage(EventId = 1216, Level = LogLevel.Warning, Message = "Could not list sandboxes; returning none: {Error}")]
    private static partial void LogListFailed(ILogger logger, string error);
}
