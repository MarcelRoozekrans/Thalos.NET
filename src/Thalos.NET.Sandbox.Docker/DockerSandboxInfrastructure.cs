using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Docker;

/// <summary>What a sandbox needs from the shared infrastructure.</summary>
/// <param name="EgressProxy">The only route out of a sandbox.</param>
/// <param name="GatewayPort">The loopback port the gateway publishes.</param>
internal sealed record SandboxInfrastructureState(Uri EgressProxy, int GatewayPort);

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
/// The internal network, the egress proxy and the gateway that every sandbox shares. Set up once, lazily, and
/// left running: the infrastructure outlives the host, and a later host adopts it.
/// </summary>
/// <remarks>
/// Each container is created on the default bridge, its config is put in by put-archive, it is connected to the
/// internal network with its alias, and then started. An alias is only accepted on the internal network; the default
/// bridge rejects network-scoped aliases. A container is adopted only when it carries this runtime's labels, the same
/// image, the same config hash, the expected port and an endpoint on the current internal network, and has been
/// started before; a labelled container that differs is replaced, and an unlabelled one is refused.
/// </remarks>
internal sealed partial class DockerSandboxInfrastructure(DockerClient docker, DockerSandboxOptions options, TimeProvider clock, ILogger logger) : IDisposable
{
    private const string GatewayContainerPort = "8080/tcp";
    private const string SquidReadyLine = "Accepting HTTP Socket connections";
    private static readonly Uri EgressUri = new("http://egress:3128");

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(2) };
    private SandboxInfrastructureState? state;

    /// <summary>The Docker client, shared with the runtime.</summary>
    public DockerClient Docker => docker;

    /// <summary>Sets the infrastructure up once; later calls return the cached state.</summary>
    public async ValueTask<Result<SandboxInfrastructureState, AgentError>> EnsureAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref state) is { } ready)
        {
            return Result<SandboxInfrastructureState, AgentError>.Success(ready);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
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

    /// <summary>Renders squid.conf with the extra egress domains, after validating them.</summary>
    internal static Result<string, AgentError> RenderSquidConf(IEnumerable<string> extraDomains)
    {
        var domains = extraDomains.ToList();
        var valid = DockerSandboxOptions.ValidateEgressDomains(domains);
        return valid.IsFailure
            ? Result<string, AgentError>.Failure(valid.Error)
            : Result<string, AgentError>.Success(ReadResource("squid.conf").Replace("{{EXTRA}}", string.Join(' ', domains), StringComparison.Ordinal));
    }

    /// <summary>The gateway's nginx server config.</summary>
    internal static string NginxConf => ReadResource("nginx.conf");

    public void Dispose()
    {
        probe.Dispose();
        gate.Dispose();
        docker.Dispose();
    }

    private async ValueTask<Result<SandboxInfrastructureState, AgentError>> SetUpAsync(CancellationToken ct)
    {
        var valid = options.Validate();
        if (valid.IsFailure)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(valid.Error);
        }

        var squid = RenderSquidConf(options.ExtraEgressDomains);
        if (squid.IsFailure)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(squid.Error);
        }

        try
        {
            var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
            if (network.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(network.Error);
            }

            var egress = new InfraContainer(SandboxLabels.RoleEgress, options.EgressContainerName, options.EgressImage, "/etc/squid", "squid.conf", squid.Value, "egress", RequestedPort: null);
            var egressResult = await EnsureContainerAsync(egress, network.Value, ct).ConfigureAwait(false);
            if (egressResult.IsFailure)
            {
                return Result<SandboxInfrastructureState, AgentError>.Failure(egressResult.Error);
            }

            var gateway = new InfraContainer(SandboxLabels.RoleGateway, options.GatewayContainerName, options.GatewayImage, "/etc/nginx/conf.d", "default.conf", NginxConf, "gateway", options.GatewayPort);
            var gatewayResult = await EnsureContainerAsync(gateway, network.Value, ct).ConfigureAwait(false);
            return gatewayResult.IsFailure
                ? Result<SandboxInfrastructureState, AgentError>.Failure(gatewayResult.Error)
                : Result<SandboxInfrastructureState, AgentError>.Success(new SandboxInfrastructureState(EgressUri, gatewayResult.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<SandboxInfrastructureState, AgentError>.Failure(AgentError.ProviderError("could not set up the sandbox network", DockerErrors.Describe(ex, options.EngineTimeout)));
        }
    }

    /// <summary>Finds or creates the internal network; returns its id.</summary>
    private async ValueTask<Result<string, AgentError>> EnsureNetworkAsync(CancellationToken ct)
    {
        var name = options.InternalNetwork;
        var found = await docker.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = DockerErrors.Filter("name", name) },
            ct).ConfigureAwait(false);

        // The name filter is a substring match.
        var existing = found.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing.Internal
                ? Result<string, AgentError>.Success(existing.ID)
                : Result<string, AgentError>.Failure(AgentError.ProviderError($"network '{name}' exists but is not internal; refusing to attach sandboxes to it"));
        }

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
        return Result<string, AgentError>.Success(created.ID);
    }

    /// <summary>Adopts or creates one infrastructure container; returns its published port, or 0 for none.</summary>
    private async ValueTask<Result<int, AgentError>> EnsureContainerAsync(InfraContainer spec, string networkId, CancellationToken ct)
    {
        var configHash = Hash(spec.Content);
        var existing = await DockerErrors.TryInspectContainerAsync(docker, spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var labels = existing.Config?.Labels;
            if (!HasLabel(labels, SandboxLabels.Sandbox, "true") || !HasLabel(labels, SandboxLabels.Role, spec.Role) || !HasLabel(labels, SandboxLabels.Network, options.InternalNetwork))
            {
                return Result<int, AgentError>.Failure(AgentError.ProviderError($"container '{spec.Name}' exists and is not this runtime's {spec.Role}; refusing to replace it"));
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
                        return Result<int, AgentError>.Failure(waited.Error);
                    }
                }

                LogAdopted(logger, spec.Role, spec.Name);
                return Result<int, AgentError>.Success(boundPort);
            }

            LogReplacing(logger, spec.Role, spec.Name, reason);
            await docker.Containers.RemoveContainerAsync(existing.ID, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
        }

        return await CreateContainerAsync(spec, configHash, networkId, ct).ConfigureAwait(false);
    }

    private async ValueTask<Result<int, AgentError>> CreateContainerAsync(InfraContainer spec, string configHash, string networkId, CancellationToken ct)
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
                return Result<int, AgentError>.Failure(ready.Error);
            }

            LogCreated(logger, spec.Role, spec.Name);
            return Result<int, AgentError>.Success(port);
        }
        catch
        {
            // A half-configured container must not be adopted next time.
            await RemoveQuietlyAsync(created.ID).ConfigureAwait(false);
            throw;
        }
    }

    private CreateContainerParameters InfraParameters(InfraContainer spec, string configHash, int port)
    {
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
            },
        };

        if (spec.RequestedPort is not null)
        {
            parameters.ExposedPorts = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal) { [GatewayContainerPort] = default };
            parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)
            {
                [GatewayContainerPort] = [new PortBinding { HostIP = "127.0.0.1", HostPort = port.ToString(CultureInfo.InvariantCulture) }],
            };
        }

        return parameters;
    }

    /// <summary>Waits until the gateway answers its own 404, or squid reports it accepts connections.</summary>
    private async ValueTask<UnitResult<AgentError>> WaitReadyAsync(InfraContainer spec, string containerId, int port, CancellationToken ct)
    {
        var deadline = clock.GetTimestamp();
        while (true)
        {
            if (string.Equals(spec.Role, SandboxLabels.RoleGateway, StringComparison.Ordinal) ? await GatewayAnswersAsync(port, ct).ConfigureAwait(false) : await SquidAcceptsAsync(containerId, ct).ConfigureAwait(false))
            {
                return UnitResult<AgentError>.Success();
            }

            var inspect = await docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            if (inspect.State?.Running != true || clock.GetElapsedTime(deadline) > options.InfrastructureReadyTimeout)
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

    private async ValueTask<bool> SquidAcceptsAsync(string containerId, CancellationToken ct)
    {
        var logs = await ReadLogsAsync(containerId, tail: "all", ct).ConfigureAwait(false);
        return logs.Contains(SquidReadyLine, StringComparison.Ordinal);
    }

    private async ValueTask<string> LogTailAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await ReadLogsAsync(containerId, tail: "20", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"(logs unavailable: {DockerErrors.Describe(ex, options.EngineTimeout)})";
        }
    }

    private async ValueTask<string> ReadLogsAsync(string containerId, string tail, CancellationToken ct)
    {
        using var stream = await docker.Containers.GetContainerLogsAsync(
            containerId,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = tail },
            ct).ConfigureAwait(false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
        return stdout + stderr;
    }

    private async ValueTask RemoveQuietlyAsync(string containerId)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>One shared infrastructure container.</summary>
    private sealed record InfraContainer(string Role, string Name, string Image, string Directory, string FileName, string Content, string Alias, int? RequestedPort);
}
