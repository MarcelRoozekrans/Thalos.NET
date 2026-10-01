using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Docker;

/// <summary>What a sandbox needs from the shared infrastructure.</summary>
/// <param name="EgressProxy">The only route out of a sandbox.</param>
/// <param name="GatewayPort">The loopback port the gateway publishes.</param>
/// <param name="NetworkId">The internal network's id. Run containers attach by id, never by name, so a same-named replacement network is never used.</param>
/// <param name="EgressContainerId">The egress proxy container.</param>
/// <param name="GatewayContainerId">The gateway container.</param>
internal sealed record SandboxInfrastructureState(Uri EgressProxy, int GatewayPort, string NetworkId, string EgressContainerId, string GatewayContainerId);

/// <summary>Label keys on every Docker object the runtime creates.</summary>
internal static class SandboxLabels
{
    public const string Sandbox = "thalos.sandbox";
    public const string Role = "thalos.sandbox.role";
    public const string RunId = "thalos.run_id";
    public const string Network = "thalos.sandbox.network";
    public const string Config = "thalos.sandbox.config";

    public const string RoleRun = "run";
    public const string RoleGateway = "gateway";
    public const string RoleEgress = "egress";
}

/// <summary>
/// The internal network, the egress proxy and the gateway that every sandbox shares. Set up lazily and left running:
/// the infrastructure outlives the host, and a later host adopts it.
/// </summary>
/// <remarks>
/// <para>
/// Each container is created on the default bridge, its config is put in by put-archive, it is connected to the
/// internal network with its alias, and then started. An alias is only accepted on the internal network; the default
/// bridge rejects network-scoped aliases. A container is adopted only when it carries this runtime's labels, the same
/// image, the same config hash, the expected port and an endpoint on the current internal network, and has been
/// started before; a labelled container that differs is replaced, and an unlabelled one is refused.
/// </para>
/// <para>
/// The cached state is re-verified on every <see cref="EnsureAsync"/>: when the network, the egress proxy or the
/// gateway is gone or stopped, the infrastructure is set up again. Every wait is bounded: the gate, the whole set-up,
/// each image pull and each log read.
/// </para>
/// </remarks>
internal sealed partial class DockerSandboxInfrastructure(DockerClient docker, DockerSandboxOptions options, TimeProvider clock, ILogger logger) : IDisposable
{
    private const string GatewayContainerPort = "8080/tcp";
    private const string SquidReadyLine = "Accepting HTTP Socket connections";

    /// <summary>Bump when the infrastructure containers' create parameters change, so older containers are replaced, not adopted.</summary>
    private const string InfraRevision = "2";

    private static readonly Uri EgressUri = new("http://egress:3128");

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(2) };
    private SandboxInfrastructureState? state;

    /// <summary>The Docker client, shared with the runtime.</summary>
    public DockerClient Docker => docker;

    /// <summary>The most one set-up may take: two pulls, two readiness waits, and a bounded number of engine calls.</summary>
    internal TimeSpan SetUpBudget => (2 * options.ImagePullTimeout) + (2 * options.InfrastructureReadyTimeout) + (20 * options.EngineTimeout);

    /// <summary>Returns verified infrastructure, setting it up when there is none or the cached one is gone.</summary>
    public async ValueTask<Result<SandboxInfrastructureState, AgentError>> EnsureAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref state) is { } cached)
        {
            var problem = await ProblemWithAsync(cached, ct).ConfigureAwait(false);
            if (problem.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(problem.Error);
            }

            if (problem.Value is null)
            {
                return Result<SandboxInfrastructureState, AgentError>.Success(cached);
            }

            Invalidate(cached, problem.Value);
        }

        if (!await gate.WaitAsync(SetUpBudget, ct).ConfigureAwait(false))
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(AgentError.ProviderError("the sandbox infrastructure is still being set up by another call"));
        }

        try
        {
            if (state is { } raced)
            {
                return Result<SandboxInfrastructureState, AgentError>.Success(raced);
            }

            var started = clock.GetTimestamp();
            var result = await SetUpAsync(ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Volatile.Write(ref state, result.Value);
                var elapsedMs = (long)clock.GetElapsedTime(started).TotalMilliseconds;
                LogReady(logger, options.InternalNetwork, result.Value.GatewayPort, elapsedMs);
            }
            else
            {
                LogFailed(logger, result.Error.Message, result.Error.Detail);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drops the cached state when it is this one and something about it is no longer true.</summary>
    /// <returns>true when <paramref name="used"/> was found broken and dropped.</returns>
    public async ValueTask<bool> RecheckAsync(SandboxInfrastructureState used, CancellationToken ct)
    {
        var problem = await ProblemWithAsync(used, ct).ConfigureAwait(false);
        if (problem.IsFailure || problem.Value is null)
        {
            return false;
        }

        Invalidate(used, problem.Value);
        return true;
    }

    /// <summary>Renders squid.conf with the extra egress domains and the internal network's subnets, after validating both.</summary>
    internal static Result<string, AgentError> RenderSquidConf(IEnumerable<string> extraDomains, IEnumerable<string> clientSubnets)
    {
        var domains = extraDomains.ToList();
        var valid = DockerSandboxOptions.ValidateEgressDomains(domains);
        if (valid.IsFailure)
        {
            return Result<string, AgentError>.Failure(valid.Error);
        }

        var subnets = new List<string>();
        foreach (var subnet in clientSubnets)
        {
            if (!IPNetwork.TryParse(subnet, out var parsed))
            {
                return Result<string, AgentError>.Failure(AgentError.ProviderError($"the internal network reports a subnet '{subnet}' that is not a CIDR range"));
            }

            subnets.Add(parsed.ToString());
        }

        if (subnets.Count == 0)
        {
            return Result<string, AgentError>.Failure(AgentError.ProviderError("the internal network reports no subnet, so the egress proxy could not be limited to sandboxes"));
        }

        return Result<string, AgentError>.Success(ReadResource("squid.conf")
            .Replace("{{EXTRA}}", string.Join(' ', domains), StringComparison.Ordinal)
            .Replace("{{SUBNETS}}", string.Join(' ', subnets), StringComparison.Ordinal));
    }

    /// <summary>
    /// The error for a set-up that was cancelled without the caller asking: the whole budget ran out, or, when the budget
    /// token was not cancelled, one engine call hit the client's own timeout.
    /// </summary>
    internal static AgentError SetUpCancelled(bool budgetExpired, TimeSpan budget, TimeSpan engineTimeout) => budgetExpired
        ? AgentError.ProviderError($"could not set up the sandbox network within {budget.TotalSeconds:0} s")
        : EngineCallTimedOut(engineTimeout);

    /// <summary>One engine call did not answer within the client timeout.</summary>
    internal static AgentError EngineCallTimedOut(TimeSpan engineTimeout) =>
        AgentError.ProviderError($"could not set up the sandbox network: a Docker engine call timed out after {engineTimeout.TotalSeconds:0} s");

    /// <summary>The gateway's nginx server config.</summary>
    internal static string NginxConf => ReadResource("nginx.conf");

    /// <summary>Turns a container's StartedAt into the engine's "since" form, seconds.nanoseconds, or null.</summary>
    internal static string? SinceOf(string? startedAt)
    {
        if (startedAt is null)
        {
            return null;
        }

        var match = Rfc3339().Match(startedAt);
        if (!match.Success
            || !DateTimeOffset.TryParse(match.Groups["s"].Value + match.Groups["z"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var whole))
        {
            return null;
        }

        var fraction = match.Groups["f"].Value.PadRight(9, '0')[..9];
        return $"{whole.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{fraction}";
    }

    public void Dispose()
    {
        probe.Dispose();
        gate.Dispose();
        docker.Dispose();
    }

    private void Invalidate(SandboxInfrastructureState stale, string reason)
    {
        if (Interlocked.CompareExchange(ref state, null, stale) == stale)
        {
            LogReEnsuring(logger, options.InternalNetwork, reason);
        }
    }

    /// <summary>Why cached infrastructure can no longer be used, null when it can, or a failure when the engine did not answer.</summary>
    private async ValueTask<Result<string?, AgentError>> ProblemWithAsync(SandboxInfrastructureState cached, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(3 * options.EngineTimeout);
        try
        {
            NetworkResponse network;
            try
            {
                network = await docker.Networks.InspectNetworkAsync(cached.NetworkId, bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (DockerErrors.IsNotFound(ex))
            {
                return Result<string?, AgentError>.Success("the internal network is gone");
            }

            if (!network.Internal || !string.Equals(network.Name, options.InternalNetwork, StringComparison.Ordinal))
            {
                return Result<string?, AgentError>.Success("the internal network changed");
            }

            foreach (var (role, id) in new[] { (SandboxLabels.RoleEgress, cached.EgressContainerId), (SandboxLabels.RoleGateway, cached.GatewayContainerId) })
            {
                var container = await DockerErrors.TryInspectContainerAsync(docker, id, bounded.Token).ConfigureAwait(false);
                if (container?.State?.Running != true)
                {
                    return Result<string?, AgentError>.Success($"the {role} container is gone or stopped");
                }
            }

            return Result<string?, AgentError>.Success(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<string?, AgentError>.Failure(AgentError.ProviderError("could not check the sandbox network", DockerErrors.Describe(ex, options.EngineTimeout)));
        }
    }

    private async ValueTask<Result<SandboxInfrastructureState, AgentError>> SetUpAsync(CancellationToken ct)
    {
        var valid = options.Validate();
        if (valid.IsFailure)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(valid.Error);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(SetUpBudget);
        try
        {
            var network = await EnsureNetworkAsync(budget.Token).ConfigureAwait(false);
            if (network.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(network.Error);
            }

            var squid = RenderSquidConf(options.ExtraEgressDomains, network.Value.Subnets);
            if (squid.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(squid.Error);
            }

            var egress = new InfraContainer(SandboxLabels.RoleEgress, options.EgressContainerName, options.EgressImage, "/etc/squid", "squid.conf", squid.Value, "egress", RequestedPort: null);
            var egressResult = await EnsureContainerAsync(egress, network.Value.Id, budget.Token).ConfigureAwait(false);
            if (egressResult.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(egressResult.Error);
            }

            var gateway = new InfraContainer(SandboxLabels.RoleGateway, options.GatewayContainerName, options.GatewayImage, "/etc/nginx/conf.d", "default.conf", NginxConf, "gateway", options.GatewayPort);
            var gatewayResult = await EnsureContainerAsync(gateway, network.Value.Id, budget.Token).ConfigureAwait(false);
            return gatewayResult.IsFailure
                ? Result<SandboxInfrastructureState, AgentError>.Failure(gatewayResult.Error)
                : Result<SandboxInfrastructureState, AgentError>.Success(new SandboxInfrastructureState(
                    EgressUri, gatewayResult.Value.Port, network.Value.Id, egressResult.Value.Id, gatewayResult.Value.Id));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(SetUpCancelled(budget.IsCancellationRequested, SetUpBudget, options.EngineTimeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(AgentError.ProviderError("could not set up the sandbox network", DockerErrors.Describe(ex, options.EngineTimeout)));
        }
    }

    /// <summary>Finds or creates the internal network; returns its id and subnets.</summary>
    private async ValueTask<Result<(string Id, IReadOnlyList<string> Subnets), AgentError>> EnsureNetworkAsync(CancellationToken ct)
    {
        var name = options.InternalNetwork;
        var found = await docker.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = DockerErrors.Filter("name", name) },
            ct).ConfigureAwait(false);

        // The name filter is a substring match.
        var network = found.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
        if (network is not null)
        {
            if (!network.Internal)
            {
                return Result<(string, IReadOnlyList<string>), AgentError>.Failure(AgentError.ProviderError($"network '{name}' exists but is not internal; refusing to attach sandboxes to it"));
            }

            if (!HasLabel(network.Labels, SandboxLabels.Sandbox, "true"))
            {
                LogAdoptingUnlabelledNetwork(logger, name);
            }
        }
        else
        {
            var created = await docker.Networks.CreateNetworkAsync(
                new NetworksCreateParameters
                {
                    Name = name,
                    Driver = "bridge",
                    Internal = true,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [SandboxLabels.Sandbox] = "true",
                        [SandboxLabels.Network] = name,
                    },
                },
                ct).ConfigureAwait(false);
            LogNetworkCreated(logger, name);
            network = await docker.Networks.InspectNetworkAsync(created.ID, ct).ConfigureAwait(false);
        }

        var subnets = network.IPAM?.Config?.Select(c => c.Subnet).Where(s => !string.IsNullOrEmpty(s)).ToList() ?? [];
        return Result<(string, IReadOnlyList<string>), AgentError>.Success((network.ID, subnets));
    }

    /// <summary>Adopts or creates one infrastructure container; returns its id and published port, or 0 for none.</summary>
    private async ValueTask<Result<(string Id, int Port), AgentError>> EnsureContainerAsync(InfraContainer spec, string networkId, CancellationToken ct)
    {
        var configHash = Hash(InfraRevision + "\n" + spec.Content);
        var existing = await DockerErrors.TryInspectContainerAsync(docker, spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var labels = existing.Config?.Labels;
            if (!HasLabel(labels, SandboxLabels.Sandbox, "true") || !HasLabel(labels, SandboxLabels.Role, spec.Role) || !HasLabel(labels, SandboxLabels.Network, options.InternalNetwork))
            {
                return Result<(string, int), AgentError>.Failure(AgentError.ProviderError($"container '{spec.Name}' exists and is not this runtime's {spec.Role}; refusing to replace it"));
            }

            var boundPort = BoundPort(existing);
            var reason = AdoptionProblem(existing, spec, configHash, networkId, boundPort);
            if (reason is null)
            {
                if (existing.State?.Running != true)
                {
                    await docker.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
                    var waited = await WaitReadyAsync(spec, existing.ID, boundPort, ct).ConfigureAwait(false);
                    if (waited.IsFailure)
                    {
                        return Result<(string, int), AgentError>.Failure(waited.Error);
                    }
                }

                LogAdopted(logger, spec.Role, spec.Name);
                return Result<(string, int), AgentError>.Success((existing.ID, boundPort));
            }

            LogReplacing(logger, spec.Role, spec.Name, reason);
            await docker.Containers.RemoveContainerAsync(existing.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, ct).ConfigureAwait(false);
        }

        var image = await EnsureImageAsync(spec.Image, ct).ConfigureAwait(false);
        return image.IsFailure
            ? Result<(string, int), AgentError>.Failure(image.Error)
            : await CreateContainerAsync(spec, configHash, networkId, ct).ConfigureAwait(false);
    }

    /// <summary>Pulls an infrastructure image that is not present. Never used for a run image.</summary>
    private async ValueTask<UnitResult<AgentError>> EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (DockerErrors.IsNotFound(ex))
        {
            // Not present: pull it below.
        }

        LogPulling(logger, image);
        string? lastError = null;
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(options.ImagePullTimeout);
            try
            {
                await docker.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = image },
                    authConfig: null,
                    new SyncProgress(message => lastError = message.Error?.Message ?? lastError),
                    bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return UnitResult<AgentError>.Failure(bounded.IsCancellationRequested
                    ? AgentError.ProviderError($"could not pull '{image}' within {options.ImagePullTimeout.TotalSeconds:0} s")
                    : EngineCallTimedOut(options.EngineTimeout));
            }
        }

        try
        {
            await docker.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (DockerErrors.IsNotFound(ex))
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not pull '{image}'", lastError));
        }
    }

    private async ValueTask<Result<(string Id, int Port), AgentError>> CreateContainerAsync(InfraContainer spec, string configHash, string networkId, CancellationToken ct)
    {
        var port = spec.RequestedPort switch
        {
            null => 0,
            0 => FreeLoopbackPort(),
            var requested => requested.Value,
        };

        var created = await docker.Containers.CreateContainerAsync(InfraParameters(spec, configHash, port), ct).ConfigureAwait(false);
        try
        {
            using (var archive = Tar(spec.FileName, spec.Content))
            {
                await docker.Containers.ExtractArchiveToContainerAsync(created.ID, new CopyToContainerParameters { Path = spec.Directory }, archive, ct).ConfigureAwait(false);
            }

            await docker.Networks.ConnectNetworkAsync(
                networkId,
                new NetworkConnectParameters { Container = created.ID, EndpointConfig = new EndpointSettings { Aliases = [spec.Alias] } },
                ct).ConfigureAwait(false);
            await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);

            var ready = await WaitReadyAsync(spec, created.ID, port, ct).ConfigureAwait(false);
            if (ready.IsFailure)
            {
                await RemoveQuietlyAsync(created.ID).ConfigureAwait(false);
                return Result<(string, int), AgentError>.Failure(ready.Error);
            }

            LogCreated(logger, spec.Role, spec.Name);
            return Result<(string, int), AgentError>.Success((created.ID, port));
        }
        catch
        {
            // A half-configured container must not be adopted next time.
            await RemoveQuietlyAsync(created.ID).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The infrastructure containers' create parameters. Both run with <c>no-new-privileges</c>, a memory and process
    /// limit, and every capability dropped except those their images need to start: verified on Engine 29.5.2, nginx
    /// needs CHOWN, SETUID and SETGID, and squid additionally DAC_OVERRIDE.
    /// </summary>
    private CreateContainerParameters InfraParameters(InfraContainer spec, string configHash, int port)
    {
        var gateway = spec.RequestedPort is not null;
        var memory = gateway ? 128L * 1024 * 1024 : 256L * 1024 * 1024;
        var parameters = new CreateContainerParameters
        {
            Name = spec.Name,
            Image = spec.Image,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SandboxLabels.Sandbox] = "true",
                [SandboxLabels.Role] = spec.Role,
                [SandboxLabels.Network] = options.InternalNetwork,
                [SandboxLabels.Config] = configHash,
            },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                SecurityOpt = ["no-new-privileges:true"],
                CapDrop = ["ALL"],
                CapAdd = gateway ? ["CHOWN", "SETUID", "SETGID"] : ["CHOWN", "SETUID", "SETGID", "DAC_OVERRIDE"],
                Memory = memory,
                MemorySwap = memory,
                PidsLimit = 256,
            },
        };

        if (gateway)
        {
            parameters.ExposedPorts = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal) { [GatewayContainerPort] = default };
            parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)
            {
                [GatewayContainerPort] = [new PortBinding { HostIP = "127.0.0.1", HostPort = port.ToString(CultureInfo.InvariantCulture) }],
            };
        }

        return parameters;
    }

    /// <summary>Waits until the gateway answers its own 404, or squid reports, since this start, that it accepts connections.</summary>
    private async ValueTask<UnitResult<AgentError>> WaitReadyAsync(InfraContainer spec, string containerId, int port, CancellationToken ct)
    {
        var since = clock.GetTimestamp();
        var startedAt = SinceOf((await docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false)).State?.StartedAt);
        while (true)
        {
            if (string.Equals(spec.Role, SandboxLabels.RoleGateway, StringComparison.Ordinal) ? await GatewayAnswersAsync(port, ct).ConfigureAwait(false) : await SquidAcceptsAsync(containerId, startedAt, ct).ConfigureAwait(false))
            {
                return UnitResult<AgentError>.Success();
            }

            var inspect = await docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            if (inspect.State?.Running != true || clock.GetElapsedTime(since) > options.InfrastructureReadyTimeout)
            {
                var tail = await LogTailAsync(containerId, ct).ConfigureAwait(false);
                var why = inspect.State?.Running == true ? $"did not become ready within {options.InfrastructureReadyTimeout.TotalSeconds:0} s" : $"exited with code {inspect.State?.ExitCode}";
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"the sandbox {spec.Role} '{spec.Name}' {why}", tail));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> GatewayAnswersAsync(int port, CancellationToken ct)
    {
        try
        {
            using var response = await probe.GetAsync(new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"), ct).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async ValueTask<bool> SquidAcceptsAsync(string containerId, string? since, CancellationToken ct)
    {
        try
        {
            var logs = await ReadLogsAsync(containerId, tail: "all", since, ct).ConfigureAwait(false);
            return logs.Contains(SquidReadyLine, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<string> LogTailAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await ReadLogsAsync(containerId, tail: "20", since: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"(logs unavailable: {DockerErrors.Describe(ex, options.EngineTimeout)})";
        }
    }

    /// <summary>Reads logs under its own engine timeout: the library streams logs with no timeout of its own.</summary>
    private async ValueTask<string> ReadLogsAsync(string containerId, string tail, string? since, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options.EngineTimeout);
        using var stream = await docker.Containers.GetContainerLogsAsync(
            containerId,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = tail, Since = since },
            bounded.Token).ConfigureAwait(false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(bounded.Token).ConfigureAwait(false);
        return stdout + stderr;
    }

    private async ValueTask RemoveQuietlyAsync(string containerId)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCleanupFailed(logger, containerId, DockerErrors.Describe(ex, options.EngineTimeout));
        }
    }

    /// <summary>Why an existing labelled container cannot be adopted, or null when it can.</summary>
    private string? AdoptionProblem(ContainerInspectResponse existing, InfraContainer spec, string configHash, string networkId, int boundPort)
    {
        if (!string.Equals(existing.Config?.Image, spec.Image, StringComparison.Ordinal))
        {
            return $"its image is '{existing.Config?.Image}', not '{spec.Image}'";
        }

        if (!HasLabel(existing.Config?.Labels, SandboxLabels.Config, configHash))
        {
            return "its config differs";
        }

        if (spec.RequestedPort is { } requested && (boundPort == 0 || (requested != 0 && requested != boundPort)))
        {
            return $"it publishes port {boundPort}, not {(requested == 0 ? "a loopback port" : requested.ToString(CultureInfo.InvariantCulture))}";
        }

        // A never-started container may be missing its config: the put-archive comes after the create.
        if (existing.State is null || string.IsNullOrEmpty(existing.State.StartedAt) || existing.State.StartedAt.StartsWith("0001-01-01", StringComparison.Ordinal))
        {
            return "it was never started";
        }

        if (existing.NetworkSettings?.Networks is not { } networks
            || !networks.TryGetValue(options.InternalNetwork, out var endpoint)
            || !string.Equals(endpoint.NetworkID, networkId, StringComparison.Ordinal)
            || endpoint.Aliases?.Contains(spec.Alias, StringComparer.Ordinal) != true)
        {
            return $"it is not attached to '{options.InternalNetwork}' as '{spec.Alias}'";
        }

        return null;
    }

    /// <summary>The loopback port the gateway publishes, or 0.</summary>
    private static int BoundPort(ContainerInspectResponse existing)
    {
        if (existing.HostConfig?.PortBindings is { } bindings
            && bindings.TryGetValue(GatewayContainerPort, out var list)
            && list is [{ HostIP: "127.0.0.1", HostPort: { } hostPort }]
            && int.TryParse(hostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return port;
        }

        return 0;
    }

    private static bool HasLabel(IDictionary<string, string>? labels, string key, string value) =>
        labels is not null && labels.TryGetValue(key, out var actual) && string.Equals(actual, value, StringComparison.Ordinal);

    /// <summary>Binds a loopback listener on port 0, reads its port, then closes it.</summary>
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static MemoryStream Tar(string fileName, string content)
    {
        var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = data,
            };
            writer.WriteEntry(entry);
        }

        archive.Position = 0;
        return archive;
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    private static string ReadResource(string name)
    {
        using var stream = typeof(DockerSandboxInfrastructure).Assembly.GetManifestResourceStream($"Thalos.Sandbox.Docker.Infrastructure.{name}")
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // squid and nginx read a CR as part of the token, so a CRLF checkout must not reach them.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?<s>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.(?<f>\d{1,9}))?(?<z>Z|[+-]\d\d:\d\d)\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Rfc3339();

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Created the internal sandbox network {Network}")]
    private static partial void LogNetworkCreated(ILogger logger, string network);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Adopted the existing sandbox {Role} container {Name}")]
    private static partial void LogAdopted(ILogger logger, string role, string name);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "Created the sandbox {Role} container {Name}")]
    private static partial void LogCreated(ILogger logger, string role, string name);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning, Message = "Replacing the sandbox {Role} container {Name}: {Reason}")]
    private static partial void LogReplacing(ILogger logger, string role, string name, string reason);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information, Message = "Sandbox infrastructure ready on network {Network}, gateway on loopback port {Port}, in {ElapsedMs} ms")]
    private static partial void LogReady(ILogger logger, string network, int port, long elapsedMs);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Warning, Message = "Sandbox infrastructure could not be set up: {Error} {Detail}")]
    private static partial void LogFailed(ILogger logger, string error, string? detail);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning, Message = "Could not remove the half-created infrastructure container {ContainerId}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string containerId, string error);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Warning, Message = "Setting the sandbox infrastructure of network {Network} up again: {Reason}")]
    private static partial void LogReEnsuring(ILogger logger, string network, string reason);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Information, Message = "Pulling the missing sandbox infrastructure image {Image}")]
    private static partial void LogPulling(ILogger logger, string image);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Warning, Message = "Adopting the internal network {Network}, which this runtime did not create: it has no thalos.sandbox label")]
    private static partial void LogAdoptingUnlabelledNetwork(ILogger logger, string network);

    /// <summary>One shared infrastructure container.</summary>
    private sealed record InfraContainer(string Role, string Name, string Image, string Directory, string FileName, string Content, string Alias, int? RequestedPort);

    /// <summary>Reports progress on the calling thread, unlike <see cref="Progress{T}"/>, so the last error is set before the pull returns.</summary>
    private sealed class SyncProgress(Action<JSONMessage> report) : IProgress<JSONMessage>
    {
        public void Report(JSONMessage value) => report(value);
    }
}
