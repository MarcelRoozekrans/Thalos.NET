using System.Formats.Tar;
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

    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..10];

    /// <summary>The fixture's main internal network.</summary>
    public string Network => $"thalos-sbx-test-{Suffix}";

    /// <summary><c>curlimages/curl:8.10.1</c> that sleeps instead of running curl.</summary>
    public string CurlImage => $"thalos-sandbox-test-curl:{Suffix}";

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
            var filter = Filter("label", $"thalos.sandbox.network={scope}");
            foreach (var container in await Docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = filter }))
            {
                await Quietly(() => Docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }));
            }

            foreach (var volume in (await Docker.Volumes.ListAsync(new VolumesListParameters { Filters = filter })).Volumes ?? [])
            {
                await Quietly(() => Docker.Volumes.RemoveAsync(volume.Name, force: true));
            }

            foreach (var network in await Docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = filter }))
            {
                await Quietly(() => Docker.Networks.DeleteNetworkAsync(network.ID));
            }
        }

        var mine = Filter("label", $"{TestLabel}={Suffix}");
        foreach (var container in await Docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = mine }))
        {
            await Quietly(() => Docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }));
        }

        foreach (var network in await Docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = mine }))
        {
            await Quietly(() => Docker.Networks.DeleteNetworkAsync(network.ID));
        }

        foreach (var image in await Docker.Images.ListImagesAsync(new ImagesListParameters { Filters = mine }))
        {
            await Quietly(() => Docker.Images.DeleteImageAsync(image.ID, new ImageDeleteParameters { Force = true }));
        }

        Docker.Dispose();
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
