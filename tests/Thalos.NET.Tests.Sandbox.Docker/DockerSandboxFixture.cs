using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>
/// One test class's Docker world: a unique network scope, unique infrastructure names and probe images, all removed
/// by label afterwards. Nothing named or labelled outside this scope is touched, so a real deployment's
/// <c>thalos-sandbox-gateway</c> and <c>thalos-sandbox-egress</c>, and any Aspire objects, are left alone.
/// </summary>
public sealed class DockerSandboxFixture : IAsyncLifetime
{
    public const string TestLabel = "thalos.sandbox.test";

    private readonly List<string> scopes = [];
    private readonly List<DockerSandboxRuntime> runtimes = [];
    private readonly List<string> imagesToRemove = [];
    private readonly List<string> volumesToRemove = [];

    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..10];

    /// <summary>The fixture's main internal network.</summary>
    public string Network => $"thalos-sbx-test-{Suffix}";

    /// <summary><c>curlimages/curl:8.10.1</c> that sleeps instead of running curl.</summary>
    public string CurlImage => $"thalos-sandbox-test-curl:{Suffix}";

    /// <summary>The curl probe with an anonymous <c>VOLUME /data</c>, which a container removal must take with it.</summary>
    public string VolumeImage => $"thalos-sandbox-test-vol:{Suffix}";

    /// <summary><c>nginx:1.27-alpine</c> serving <c>probe uri=$request_uri</c> on 8080, as user 10001 on a read-only rootfs.</summary>
    public string NginxImage => $"thalos-sandbox-test-nginx:{Suffix}";

    public DockerClient Docker { get; private set; } = null!;

    /// <summary>The runtime of the main network, created on first use.</summary>
    public DockerSandboxRuntime Runtime => runtime ??= NewRuntime(Options());

    private DockerSandboxRuntime? runtime;

    /// <summary>Options whose network and infrastructure names are unique to this fixture.</summary>
    public DockerSandboxOptions Options(string? network = null)
    {
        var name = network ?? Network;
        return new DockerSandboxOptions
        {
            InternalNetwork = name,
            GatewayContainerName = $"{name}-gateway",
            EgressContainerName = $"{name}-egress",
        };
    }

    /// <summary>A runtime whose objects are removed when the fixture is disposed.</summary>
    public DockerSandboxRuntime NewRuntime(DockerSandboxOptions options)
    {
        lock (scopes)
        {
            if (!scopes.Contains(options.InternalNetwork))
            {
                scopes.Add(options.InternalNetwork);
            }

            var created = new DockerSandboxRuntime(options, TimeProvider.System, NullLogger<DockerSandboxRuntime>.Instance);
            runtimes.Add(created);
            return created;
        }
    }

    public static SandboxSpec Spec(string image) => new()
    {
        RunId = Guid.NewGuid(),
        Image = image,
        Token = "test-token",
        AllowedWriteExtensions = null,
        ProtectedPaths = new ProtectedPathSet([".github/"]),
        Limits = new SandboxLimits(Cpus: 1, MemoryBytes: 256L * 1024 * 1024, PidsLimit: 128, TmpfsBytes: 16L * 1024 * 1024),
    };

    /// <summary>Runs a command in a container and returns its exit code and output. Bounded by one minute in all, hijacked stream included.</summary>
    public async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(string container, params string[] command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var exec = await Docker.Exec.CreateContainerExecAsync(container, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = command,
        }, timeout.Token);
        using var stream = await Docker.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), timeout.Token);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(timeout.Token);
        var inspect = await Docker.Exec.InspectContainerExecAsync(exec.ID, timeout.Token);
        return (inspect.ExitCode ?? -1, stdout, stderr);
    }

    public async Task InitializeAsync()
    {
        if (!DockerAvailable.Value)
        {
            return;
        }

        Docker = new DockerClientBuilder().WithTimeout(TimeSpan.FromMinutes(2)).Build();
        await BuildImageAsync(CurlImage, """
            FROM curlimages/curl:8.10.1
            ENTRYPOINT ["sleep", "3600"]
            """);
        await BuildImageAsync(VolumeImage, $"""
            FROM {CurlImage}
            VOLUME /data
            """);
        await BuildImageAsync(NginxImage, """
            FROM nginx:1.27-alpine
            COPY probe.conf /etc/nginx/probe.conf
            ENTRYPOINT ["nginx", "-c", "/etc/nginx/probe.conf", "-e", "stderr"]
            """,
            ("probe.conf", """
            daemon off;
            worker_processes 1;
            pid /tmp/nginx.pid;
            error_log stderr;
            events {}
            http {
                access_log off;
                client_body_temp_path /tmp/cb;
                proxy_temp_path /tmp/px;
                fastcgi_temp_path /tmp/fc;
                uwsgi_temp_path /tmp/uw;
                scgi_temp_path /tmp/sc;
                server {
                    listen 8080;
                    location / { default_type text/plain; return 200 "probe uri=$request_uri"; }
                }
            }
            """));
    }

    /// <summary>
    /// Removes everything this fixture made. Each step is bounded and isolated, so one slow or failing step does not
    /// leave the rest behind.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!DockerAvailable.Value)
        {
            return;
        }

        foreach (var created in runtimes)
        {
            await created.DisposeAsync();
        }

        foreach (var scope in scopes)
        {
            await BoundedAsync(ct => RemoveLabelledAsync($"thalos.sandbox.network={scope}", ct));
        }

        await BoundedAsync(ct => RemoveLabelledAsync($"{TestLabel}={Suffix}", ct));

        foreach (var volume in volumesToRemove)
        {
            await BoundedAsync(ct => Quietly(() => Docker.Volumes.RemoveAsync(volume, force: true, ct)));
        }

        foreach (var image in imagesToRemove)
        {
            await BoundedAsync(ct => Quietly(() => Docker.Images.DeleteImageAsync(image, new ImageDeleteParameters { Force = true }, ct)));
        }

        await BoundedAsync(async ct =>
        {
            foreach (var image in await Docker.Images.ListImagesAsync(new ImagesListParameters { Filters = Filter("label", $"{TestLabel}={Suffix}") }, ct))
            {
                await Quietly(() => Docker.Images.DeleteImageAsync(image.ID, new ImageDeleteParameters { Force = true }, ct));
            }
        });

        Docker.Dispose();
    }

    /// <summary>A volume a test may leave without labels, to delete by exact name when the fixture is disposed.</summary>
    public void RemoveVolumeAfterwards(string name)
    {
        lock (scopes)
        {
            volumesToRemove.Add(name);
        }
    }

    /// <summary>An image a test pulled, to delete when the fixture is disposed.</summary>
    public void RemoveImageAfterwards(string image)
    {
        lock (scopes)
        {
            imagesToRemove.Add(image);
        }
    }

    /// <summary>Starts a probe container on the default bridge, outside every sandbox network; removed by the test label.</summary>
    public async Task<string> RunOnBridgeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var name = $"thalos-sbx-test-outsider-{Guid.NewGuid():N}";
        await Docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = CurlImage,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [TestLabel] = Suffix },
            HostConfig = new HostConfig { NetworkMode = "bridge" },
        }, timeout.Token);
        await Docker.Containers.StartContainerAsync(name, new ContainerStartParameters(), timeout.Token);
        return name;
    }

    /// <summary>Sends one raw GET, path untouched by any client normalisation, and returns the status code.</summary>
    public static async Task<int> RawStatusAsync(int port, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"), timeout.Token);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(timeout.Token) ?? "";
        return int.Parse(status.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    private static async Task BoundedAsync(Func<CancellationToken, Task> step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await step(timeout.Token);
        }
        catch (Exception ex)
        {
            // Best effort: one failed or timed-out step must not stop the others.
            await Console.Error.WriteLineAsync($"Sandbox test clean-up step failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RemoveLabelledAsync(string label, CancellationToken ct)
    {
        var filter = Filter("label", label);
        foreach (var container in await Docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = filter }, ct))
        {
            await Quietly(() => Docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, ct));
        }

        foreach (var volume in (await Docker.Volumes.ListAsync(new VolumesListParameters { Filters = filter }, ct)).Volumes ?? [])
        {
            await Quietly(() => Docker.Volumes.RemoveAsync(volume.Name, force: true, ct));
        }

        foreach (var network in await Docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = filter }, ct))
        {
            await Quietly(() => Docker.Networks.DeleteNetworkAsync(network.ID, ct));
        }
    }

    public static IDictionary<string, IDictionary<string, bool>> Filter(string key, string value) =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal) { [key] = new Dictionary<string, bool>(StringComparer.Ordinal) { [value] = true } };

    private async Task BuildImageAsync(string tag, string dockerfile, params (string Name, string Content)[] files)
    {
        using var context = new MemoryStream();
        using (var writer = new TarWriter(context, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files.Prepend(("Dockerfile", dockerfile)))
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n")));
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        }

        context.Position = 0;
        await Docker.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters
            {
                Tags = [tag],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [TestLabel] = Suffix },
                Remove = true,
                ForceRemove = true,
            },
            context,
            authConfigs: null,
            headers: null,
            progress: new Progress<JSONMessage>(),
            CancellationToken.None);
    }

    private static async Task Quietly(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException)
        {
            // Best effort: already gone, or still in use by something this scope does not own.
        }
    }
}
