using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>
/// Against a real Docker engine; every test skips without one. The fixture gives this class a unique network,
/// unique gateway and egress names, and removes everything it made by label.
/// </summary>
public sealed class DockerSandboxRuntimeTests(DockerSandboxFixture fixture) : IClassFixture<DockerSandboxFixture>, IDisposable
{
    // Every engine call a test makes is bounded: xUnit makes one instance per test, so this is a per-test deadline.
    private readonly CancellationTokenSource bound = new(TimeSpan.FromMinutes(3));

    private CancellationToken Ct => bound.Token;

    public void Dispose() => bound.Dispose();

    /// <summary>
    /// Red, one per flag, by commenting it out of <c>ContainerParameters</c>: CapDrop, SecurityOpt, ReadonlyRootfs,
    /// PidsLimit, MemorySwap, NanoCPUs, Tmpfs, Mounts, NetworkMode and User; NetworkMode by name instead of id. Memory: halve it, because the engine
    /// refuses a MemorySwap without a Memory at least as large. AutoRemove: set it. Privileged, a port binding, and a
    /// docker.sock bind: add one.
    /// </summary>
    [SkippableFact]
    public async Task The_container_runs_with_the_hardening_flags()
    {
        Skip.IfNot(DockerAvailable.Value);
        var spec = DockerSandboxFixture.Spec(fixture.CurlImage);
        var handle = await CreateAsync(spec);

        var inspect = await fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(handle.SandboxId), Ct);
        var host = inspect.HostConfig!;

        host.CapDrop.Should().Equal("ALL");
        host.SecurityOpt.Should().Contain("no-new-privileges:true");
        host.ReadonlyRootfs.Should().BeTrue();
        host.PidsLimit.Should().Be(spec.Limits.PidsLimit);
        host.Memory.Should().Be(spec.Limits.MemoryBytes);
        host.MemorySwap.Should().Be(spec.Limits.MemoryBytes);
        host.NanoCPUs.Should().Be(1_000_000_000);
        host.Tmpfs.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp"] = $"rw,noexec,nosuid,size={spec.Limits.TmpfsBytes}" });
        host.Privileged.Should().BeFalse();
        host.AutoRemove.Should().BeFalse();
        inspect.Config!.User.Should().Be("10001:10001");

        var network = await fixture.Docker.Networks.InspectNetworkAsync(fixture.Network, Ct);
        network.Internal.Should().BeTrue();
        host.NetworkMode.Should().Be(network.ID);
        inspect.NetworkSettings!.Networks!.Keys.Should().Equal(fixture.Network);

        (host.PortBindings ?? new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)).Should().BeEmpty();
        (inspect.NetworkSettings!.Ports ?? new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)).Values.Should().AllSatisfy(b => (b ?? []).Should().BeEmpty());
        (host.Binds ?? []).Should().BeEmpty();
        (host.Devices ?? []).Should().BeEmpty();
        inspect.Mounts!.Should().NotContain(m => (m.Source ?? "").Contains("docker.sock", StringComparison.Ordinal));
        inspect.Mounts.Should().ContainSingle()
            .Which.Should().Match<MountPoint>(m => m.Type == "volume" && m.Name == DockerSandboxRuntime.VolumeName(handle.SandboxId) && m.Destination == "/work");
    }

    /// <summary>
    /// S1, keys and values. Red: pass the process environment through, appending Environment.GetEnvironmentVariables() to Env. Leave
    /// out PATH and names Docker cannot hold, or the container fails to start and the red is for the wrong reason.
    /// </summary>
    [SkippableFact]
    public async Task The_environment_is_only_the_specs()
    {
        Skip.IfNot(DockerAvailable.Value);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "must-not-reach-the-sandbox");
        try
        {
            var spec = DockerSandboxFixture.Spec(fixture.CurlImage);
            var handle = await CreateAsync(spec);

            var image = await fixture.Docker.Images.InspectImageAsync(fixture.CurlImage, Ct);
            var container = await fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(handle.SandboxId), Ct);

            // The image's declared pairs, overridden by the spec's: exactly what the engine merges, keys and values.
            var expected = image.Config!.Env!.ToDictionary(Key, Value, StringComparer.Ordinal);
            foreach (var (key, value) in spec.Environment(new Uri("http://egress:3128")))
            {
                expected[key] = value;
            }

            container.Config!.Env!.ToDictionary(Key, Value, StringComparer.Ordinal).Should().Equal(expected);
            container.Config.Env.Should().NotContain(e => e.StartsWith("GITHUB_TOKEN=", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }

        static string Key(string entry) => entry[..entry.IndexOf('=', StringComparison.Ordinal)];
        static string Value(string entry) => entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..];
    }

    /// <summary>S2. Red: create the network without Internal.</summary>
    [SkippableFact]
    public async Task A_sandbox_cannot_reach_the_internet_directly()
    {
        Skip.IfNot(DockerAvailable.Value);
        var handle = await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));

        var (exit, _, stderr) = await fixture.ExecAsync(DockerSandboxRuntime.ContainerName(handle.SandboxId), "curl", "-m", "5", "--noproxy", "*", "-sS", "-o", "/dev/null", "https://example.com");

        // 6 could not resolve, 7 could not connect, 28 timed out: each means no route, not a broken probe.
        exit.Should().BeOneOf([6L, 7L, 28L], stderr);
    }

    /// <summary>S2. Red: add example.com to the ACL in squid.conf; for the port case, drop the Safe_ports deny.</summary>
    [SkippableFact]
    public async Task A_sandbox_reaches_nuget_through_the_egress_proxy_and_nothing_else()
    {
        Skip.IfNot(DockerAvailable.Value);
        var handle = await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));
        var name = DockerSandboxRuntime.ContainerName(handle.SandboxId);

        var nuget = await fixture.ExecAsync(name, "curl", "-m", "20", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "https://api.nuget.org/v3/index.json");
        nuget.Stdout.Should().Be("200", nuget.Stderr);

        var other = await fixture.ExecAsync(name, "curl", "-m", "20", "-sS", "-o", "/dev/null", "https://example.com");
        other.ExitCode.Should().NotBe(0);
        other.Stderr.Should().Contain("CONNECT tunnel failed, response 403");

        // A plain-HTTP request to an allowed domain on a port other than 80 or 443 is refused by the proxy itself.
        // curl reads only a lowercase http_proxy for http:// URLs, so the proxy is given explicitly.
        var port = await fixture.ExecAsync(name, "curl", "-m", "20", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "-x", "http://egress:3128", "http://api.nuget.org:8443/");
        port.Stdout.Should().Be("403", port.Stderr);
    }

    /// <summary>
    /// Red: loosen the location regex to <c>(?&lt;sid&gt;[^/]+)</c>, take the remainder from the decoded <c>$uri</c>, or
    /// drop the check that the raw id equals the matched id.
    /// </summary>
    [SkippableFact]
    public async Task The_gateway_reaches_the_sandbox_by_id_and_nothing_else()
    {
        Skip.IfNot(DockerAvailable.Value);
        var handle = await CreateAsync(DockerSandboxFixture.Spec(fixture.NginxImage));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var root = await GetWhenUpAsync(http, handle.BaseAddress);
        root.StatusCode.Should().Be(HttpStatusCode.OK);
        (await root.Content.ReadAsStringAsync()).Should().Be("probe uri=/");

        var encoded = await http.GetAsync(new Uri(handle.BaseAddress, "a%2Fb%20c?q=%20x"));
        (await encoded.Content.ReadAsStringAsync()).Should().Be("probe uri=/a%2Fb%20c?q=%20x");

        var gateway = handle.BaseAddress.GetLeftPart(UriPartial.Authority);
        (await http.GetAsync(new Uri($"{gateway}/sandboxes/not-a-hex-id-not-a-hex-id-0123456/"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync(new Uri($"{gateway}/sandboxes/{handle.SandboxId.ToUpperInvariant()}/"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync(new Uri($"{gateway}/sandboxes/{handle.SandboxId}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync(new Uri($"{gateway}/"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Dot segments, sent raw so no client normalises them. nginx matches on the normalised path, so leaving the id
        // leaves the route, and a raw id that normalises onto another id is refused: the upstream host is always the
        // id the client wrote, and it is 32 hex.
        var port = handle.BaseAddress.Port;
        var other = Guid.NewGuid().ToString("N");
        (await DockerSandboxFixture.RawStatusAsync(port, $"/sandboxes/{handle.SandboxId}/../../etc/passwd")).Should().Be(404);
        (await DockerSandboxFixture.RawStatusAsync(port, $"/sandboxes/{handle.SandboxId}/%2e%2e/%2e%2e/etc/passwd")).Should().Be(404);
        (await DockerSandboxFixture.RawStatusAsync(port, $"/sandboxes/{other}/%2e%2e/{handle.SandboxId}/")).Should().Be(404);
        (await DockerSandboxFixture.RawStatusAsync(port, $"/sandboxes/{handle.SandboxId}/x/%2e%2e/")).Should().Be(200);
    }

    /// <summary>Red: keep the list in memory, so a new instance returns nothing.</summary>
    [SkippableFact]
    public async Task List_finds_a_sandbox_after_a_new_runtime_instance()
    {
        Skip.IfNot(DockerAvailable.Value);
        var spec = DockerSandboxFixture.Spec(fixture.CurlImage);
        var created = await CreateAsync(spec);

        var fresh = fixture.NewRuntime(fixture.Options());
        var listed = await fresh.ListAsync(Ct);

        var found = listed.Should().ContainSingle(h => h.SandboxId == created.SandboxId).Which;
        found.RunId.Should().Be(spec.RunId);
        found.State.Should().Be(SandboxState.Running);
        found.BaseAddress.Should().Be(created.BaseAddress);
        found.CreatedAt.Should().Be(created.CreatedAt);
        (await fresh.GetAsync(created.SandboxId, Ct)).Should().Be(found);
    }

    /// <summary>
    /// Another deployment's sandbox must be invisible, or a reconcile would delete it. Red: drop the network label from
    /// the list filter, or the network check from the ownership test.
    /// </summary>
    [SkippableFact]
    public async Task A_sandbox_of_another_network_is_neither_listed_nor_deleted()
    {
        Skip.IfNot(DockerAvailable.Value);
        var runId = Guid.NewGuid();
        var sandboxId = runId.ToString("N");
        await fixture.Docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = DockerSandboxRuntime.ContainerName(sandboxId),
            Image = fixture.CurlImage,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["thalos.sandbox"] = "true",
                ["thalos.sandbox.role"] = "run",
                ["thalos.run_id"] = runId.ToString("D"),
                ["thalos.sandbox.network"] = $"{fixture.Network}-elsewhere",
                [DockerSandboxFixture.TestLabel] = fixture.Suffix,
            },
        }, Ct);

        (await fixture.Runtime.ListAsync(Ct)).Should().NotContain(h => h.SandboxId == sandboxId);
        (await fixture.Runtime.GetAsync(sandboxId, Ct)).Should().BeNull();
        (await fixture.Runtime.DeleteAsync(sandboxId, Ct)).IsFailure.Should().BeTrue();
        (await fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(sandboxId), Ct)).Should().NotBeNull();
    }

    /// <summary>
    /// Red: skip the volume removal, remove the container without RemoveVolumes so the image's anonymous volume stays,
    /// or treat the second delete's 404 as a failure.
    /// </summary>
    [SkippableFact]
    public async Task Delete_is_idempotent_and_removes_the_volume()
    {
        Skip.IfNot(DockerAvailable.Value);
        var handle = await CreateAsync(DockerSandboxFixture.Spec(fixture.VolumeImage));
        var inspect = await fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(handle.SandboxId), Ct);
        var anonymous = inspect.Mounts!.Single(m => string.Equals(m.Destination, "/data", StringComparison.Ordinal)).Name!;

        (await fixture.Runtime.DeleteAsync(handle.SandboxId, Ct)).IsSuccess.Should().BeTrue();

        await FluentActions.Awaiting(() => fixture.Docker.Volumes.InspectAsync(anonymous, Ct))
            .Should().ThrowAsync<DockerApiException>().Where(e => e.StatusCode == HttpStatusCode.NotFound);

        await FluentActions.Awaiting(() => fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(handle.SandboxId), Ct))
            .Should().ThrowAsync<DockerContainerNotFoundException>();
        await FluentActions.Awaiting(() => fixture.Docker.Volumes.InspectAsync(DockerSandboxRuntime.VolumeName(handle.SandboxId), Ct))
            .Should().ThrowAsync<DockerApiException>().Where(e => e.StatusCode == HttpStatusCode.NotFound);
        (await fixture.Runtime.GetAsync(handle.SandboxId, Ct)).Should().BeNull();

        (await fixture.Runtime.DeleteAsync(handle.SandboxId, Ct)).IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: skip the clean-up after a failed create, so the volume stays.</summary>
    [SkippableFact]
    public async Task A_failed_create_removes_what_it_made()
    {
        Skip.IfNot(DockerAvailable.Value);
        var spec = DockerSandboxFixture.Spec($"thalos-sandbox-test-missing:{fixture.Suffix}");

        var result = await fixture.Runtime.CreateAsync(spec, Ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        result.Error.Message.Should().StartWith("could not create the run's sandbox: ");
        await FluentActions.Awaiting(() => fixture.Docker.Volumes.InspectAsync(DockerSandboxRuntime.VolumeName(spec.SandboxId), Ct))
            .Should().ThrowAsync<DockerApiException>().Where(e => e.StatusCode == HttpStatusCode.NotFound);
    }

    /// <summary>S2. Red: drop the Internal check on an existing network.</summary>
    [SkippableFact]
    public async Task An_existing_network_that_is_not_internal_is_refused()
    {
        Skip.IfNot(DockerAvailable.Value);
        var name = $"{fixture.Network}-open";
        await fixture.Docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Internal = false,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [DockerSandboxFixture.TestLabel] = fixture.Suffix },
        }, Ct);

        var result = await fixture.NewRuntime(fixture.Options(name)).CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage), Ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain($"network '{name}' exists but is not internal; refusing to attach sandboxes to it");
    }

    /// <summary>Red: bind the gateway on 0.0.0.0, publish a port from the egress proxy, or drop an infrastructure hardening flag.</summary>
    [SkippableFact]
    public async Task The_gateway_publishes_only_on_loopback_and_the_egress_proxy_publishes_nothing()
    {
        Skip.IfNot(DockerAvailable.Value);
        await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));
        var options = fixture.Options();

        var gateway = await fixture.Docker.Containers.InspectContainerAsync(options.GatewayContainerName, Ct);
        var egress = await fixture.Docker.Containers.InspectContainerAsync(options.EgressContainerName, Ct);

        gateway.HostConfig!.PortBindings!.Keys.Should().Equal("8080/tcp");
        gateway.HostConfig.PortBindings["8080/tcp"].Should().ContainSingle().Which.HostIP.Should().Be("127.0.0.1");
        (egress.HostConfig!.PortBindings ?? new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)).Should().BeEmpty();
        foreach (var infra in new[] { gateway, egress })
        {
            infra.HostConfig!.SecurityOpt.Should().Contain("no-new-privileges:true");
            infra.HostConfig.CapDrop.Should().Equal("ALL");
            infra.HostConfig.Memory.Should().BePositive();
            infra.HostConfig.Privileged.Should().BeFalse();
        }
    }

    /// <summary>
    /// S2, the reverse-DNS bypass: without <c>dstdomain -n</c>, squid matches an IP-literal destination by its PTR name,
    /// and 8.8.8.8's PTR is dns.google. Red: drop <c>-n</c> from squid.conf. That red shows only where the egress
    /// proxy's resolver answers PTR queries: Docker Desktop's resolver does not, so there the test stays green without
    /// <c>-n</c> and <c>Squid_conf_is_pinned</c> is the guard. With a PTR-answering resolver the bypass reproduces.
    /// </summary>
    [SkippableFact]
    public async Task An_ip_literal_is_refused_even_when_its_reverse_dns_name_is_allowed()
    {
        Skip.IfNot(DockerAvailable.Value);
        var options = fixture.Options($"{fixture.Network}-dns");
        options.ExtraEgressDomains.Add("dns.google");
        var runtime = fixture.NewRuntime(options);
        var result = await runtime.CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage), Ct);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message + " " + result.Error.Detail : "");
        var name = DockerSandboxRuntime.ContainerName(result.Value.SandboxId);

        var byName = await fixture.ExecAsync(name, "curl", "-m", "20", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "https://dns.google/");
        byName.Stdout.Should().Be("200", byName.Stderr);

        var byAddress = await fixture.ExecAsync(name, "curl", "-m", "20", "-sS", "-o", "/dev/null", "https://8.8.8.8/");
        byAddress.Stderr.Should().Contain("CONNECT tunnel failed, response 403");
    }

    /// <summary>The proxy serves only the internal network. Red: drop <c>http_access deny !sandboxes</c>.</summary>
    [SkippableFact]
    public async Task A_container_on_the_default_bridge_cannot_use_the_egress_proxy()
    {
        Skip.IfNot(DockerAvailable.Value);
        await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));
        var egress = await fixture.Docker.Containers.InspectContainerAsync(fixture.Options().EgressContainerName, Ct);
        var bridgeAddress = egress.NetworkSettings!.Networks!["bridge"].IPAddress;
        var outsider = await fixture.RunOnBridgeAsync();

        var attempt = await fixture.ExecAsync(outsider, "curl", "-m", "20", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "-x", $"http://{bridgeAddress}:3128", "http://api.nuget.org/v3/index.json");

        attempt.Stdout.Should().Be("403", attempt.Stderr);
    }

    /// <summary>
    /// Run containers attach by network id, and a replaced network is re-checked. Red: attach by name and skip the
    /// re-check, so the same-named open network is used.
    /// </summary>
    [SkippableFact]
    public async Task A_network_replaced_by_an_open_one_is_never_used()
    {
        Skip.IfNot(DockerAvailable.Value);
        var options = fixture.Options($"{fixture.Network}-swap");
        var runtime = fixture.NewRuntime(options);
        var first = await runtime.CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage), Ct);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Message + " " + first.Error.Detail : "");
        (await runtime.DeleteAsync(first.Value.SandboxId, Ct)).IsSuccess.Should().BeTrue();

        // Take the internal network away and put an open one with the same name in its place.
        foreach (var infra in new[] { options.GatewayContainerName, options.EgressContainerName })
        {
            await fixture.Docker.Networks.DisconnectNetworkAsync(options.InternalNetwork, new NetworkDisconnectParameters { Container = infra, Force = true }, Ct);
        }

        await fixture.Docker.Networks.DeleteNetworkAsync(options.InternalNetwork, Ct);
        await fixture.Docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = options.InternalNetwork,
            Internal = false,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [DockerSandboxFixture.TestLabel] = fixture.Suffix },
        }, Ct);

        var spec = DockerSandboxFixture.Spec(fixture.CurlImage);
        var second = await runtime.CreateAsync(spec, Ct);

        second.IsFailure.Should().BeTrue();
        second.Error.Message.Should().Contain("exists but is not internal");
        await FluentActions.Awaiting(() => fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(spec.SandboxId), Ct))
            .Should().ThrowAsync<DockerContainerNotFoundException>();
    }

    /// <summary>A gateway removed behind the runtime's back is set up again. Red: return the cached state without checking it.</summary>
    [SkippableFact]
    public async Task A_removed_gateway_is_set_up_again()
    {
        Skip.IfNot(DockerAvailable.Value);
        var options = fixture.Options($"{fixture.Network}-gone");
        var runtime = fixture.NewRuntime(options);
        var first = await runtime.CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage), Ct);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Message + " " + first.Error.Detail : "");

        await fixture.Docker.Containers.RemoveContainerAsync(options.GatewayContainerName, new ContainerRemoveParameters { Force = true }, Ct);

        var second = await runtime.CreateAsync(DockerSandboxFixture.Spec(fixture.NginxImage), Ct);
        second.IsSuccess.Should().BeTrue(second.IsFailure ? second.Error.Message + " " + second.Error.Detail : "");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        (await GetWhenUpAsync(http, second.Value.BaseAddress)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A labelled leftover work volume of the same run is replaced, never reused; an unlabelled one is refused and kept.
    /// Red: reuse the labelled volume, or delete the unlabelled one.
    /// </summary>
    [SkippableFact]
    public async Task A_leftover_work_volume_is_replaced_only_when_it_is_ours()
    {
        Skip.IfNot(DockerAvailable.Value);
        await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));

        var ours = DockerSandboxFixture.Spec(fixture.CurlImage);
        await fixture.Docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = DockerSandboxRuntime.VolumeName(ours.SandboxId),
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["thalos.sandbox"] = "true",
                ["thalos.sandbox.role"] = "run",
                ["thalos.run_id"] = ours.RunId.ToString("D"),
                ["thalos.sandbox.network"] = fixture.Network,
                ["stale"] = "yes",
            },
        }, Ct);
        await CreateAsync(ours);
        var replaced = await fixture.Docker.Volumes.InspectAsync(DockerSandboxRuntime.VolumeName(ours.SandboxId), Ct);
        replaced.Labels.Should().NotContainKey("stale");

        var foreign = DockerSandboxFixture.Spec(fixture.CurlImage);
        await fixture.Docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = DockerSandboxRuntime.VolumeName(foreign.SandboxId),
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [DockerSandboxFixture.TestLabel] = fixture.Suffix },
        }, Ct);
        var refused = await fixture.Runtime.CreateAsync(foreign, Ct);
        refused.IsFailure.Should().BeTrue();
        (await fixture.Docker.Volumes.InspectAsync(DockerSandboxRuntime.VolumeName(foreign.SandboxId), Ct)).Labels.Should().ContainKey(DockerSandboxFixture.TestLabel);
    }

    /// <summary>
    /// A missing gateway image is pulled. Uses <c>nginx:1.27-alpine-slim</c> and removes it afterwards; skips when it is
    /// already present, because then nothing would be pulled. Red: skip the pull.
    /// </summary>
    [SkippableFact]
    public async Task A_missing_infrastructure_image_is_pulled()
    {
        Skip.IfNot(DockerAvailable.Value);
        const string image = "nginx:1.27-alpine-slim";
        var present = true;
        try
        {
            await fixture.Docker.Images.InspectImageAsync(image, Ct);
        }
        catch (DockerImageNotFoundException)
        {
            present = false;
        }

        Skip.If(present, $"{image} is already present, so a pull cannot be observed");
        fixture.RemoveImageAfterwards(image);
        var options = fixture.Options($"{fixture.Network}-pull");
        options.GatewayImage = image;

        var result = await fixture.NewRuntime(options).CreateAsync(DockerSandboxFixture.Spec(fixture.NginxImage), Ct);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message + " " + result.Error.Detail : "");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        (await GetWhenUpAsync(http, result.Value.BaseAddress)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Concurrent creates of one sandbox. Create A pauses after making the work volume; create B is let go. Unserialised,
    /// B takes A's fresh volume for a leftover and deletes it, A's container then gets an unlabelled volume the engine
    /// makes on the fly, and DeleteAsync would later refuse it. Serialised, B waits for A, then finds A's container
    /// and fails without touching it. Red: drop the per-sandbox lock.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_creates_of_one_sandbox_leave_the_winner_intact()
    {
        Skip.IfNot(DockerAvailable.Value);
        var runtime = fixture.NewRuntime(fixture.Options());
        var spec = DockerSandboxFixture.Spec(fixture.CurlImage);
        fixture.RemoveVolumeAfterwards(DockerSandboxRuntime.VolumeName(spec.SandboxId));
        var who = new AsyncLocal<string>();
        var aPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bPrechecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.StageReached = async (stage, ct) =>
        {
            if (string.Equals(who.Value, "A", StringComparison.Ordinal) && string.Equals(stage, "volume-created", StringComparison.Ordinal))
            {
                aPaused.TrySetResult();
                await releaseA.Task.WaitAsync(ct);
            }
            else if (string.Equals(who.Value, "B", StringComparison.Ordinal) && string.Equals(stage, "prechecked", StringComparison.Ordinal))
            {
                bPrechecked.TrySetResult();
                await releaseB.Task.WaitAsync(ct);
            }
        };

        var a = Task.Run(async () => { who.Value = "A"; return await runtime.CreateAsync(spec, Ct); });
        await aPaused.Task.WaitAsync(TimeSpan.FromMinutes(1));
        var b = Task.Run(async () => { who.Value = "B"; return await runtime.CreateAsync(spec, Ct); });

        // Unserialised, B reaches its pre-check while A is paused; serialised, it cannot, and this wait times out.
        await Task.WhenAny(bPrechecked.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        releaseA.TrySetResult();
        var resultA = await a.WaitAsync(TimeSpan.FromMinutes(1));
        releaseB.TrySetResult();
        var resultB = await b.WaitAsync(TimeSpan.FromMinutes(1));

        new[] { resultA, resultB }.Count(r => r.IsSuccess).Should().Be(1);
        var container = await fixture.Docker.Containers.InspectContainerAsync(DockerSandboxRuntime.ContainerName(spec.SandboxId), Ct);
        container.State!.Running.Should().BeTrue();
        var volume = await fixture.Docker.Volumes.InspectAsync(DockerSandboxRuntime.VolumeName(spec.SandboxId), Ct);
        (volume.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal)).Should().Contain("thalos.run_id", spec.RunId.ToString("D"));
        container.Mounts!.Should().ContainSingle(m => string.Equals(m.Name, volume.Name, StringComparison.Ordinal));
        (await runtime.DeleteAsync(spec.SandboxId, Ct)).IsSuccess.Should().BeTrue();
    }

    /// <summary>Needs no engine. Red: let a library exception escape CreateAsync.</summary>
    [Fact]
    public async Task An_unreachable_engine_fails_create_without_throwing()
    {
        var options = new DockerSandboxOptions { Endpoint = new Uri("tcp://127.0.0.1:1"), EngineTimeout = TimeSpan.FromSeconds(3) };
        await using var runtime = new DockerSandboxRuntime(options, TimeProvider.System, NullLogger<DockerSandboxRuntime>.Instance);
        var watch = Stopwatch.StartNew();

        var result = await runtime.CreateAsync(DockerSandboxFixture.Spec("img:1"), Ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        result.Error.Message.Should().StartWith("could not create the run's sandbox: ");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        (await runtime.ListAsync(Ct)).Should().BeEmpty();
        (await runtime.GetAsync(Guid.NewGuid().ToString("N"), Ct)).Should().BeNull();
        (await runtime.DeleteAsync(Guid.NewGuid().ToString("N"), Ct)).IsFailure.Should().BeTrue();
    }

    /// <summary>Needs no engine. Red: skip spec.Validate in CreateAsync.</summary>
    [Fact]
    public async Task An_invalid_spec_is_refused_before_the_engine_is_called()
    {
        await using var runtime = new DockerSandboxRuntime(new DockerSandboxOptions { Endpoint = new Uri("tcp://127.0.0.1:1") }, TimeProvider.System, NullLogger<DockerSandboxRuntime>.Instance);
        var spec = DockerSandboxFixture.Spec("img:1") with { AllowedWriteExtensions = new HashSet<string>(StringComparer.Ordinal) { "*" } };

        var result = await runtime.CreateAsync(spec, Ct);

        result.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    /// <summary>Creates a sandbox, failing with the runtime's own error rather than a bare Value access.</summary>
    private async Task<SandboxHandle> CreateAsync(SandboxSpec spec)
    {
        var result = await fixture.Runtime.CreateAsync(spec, Ct);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"{result.Error.Message} {result.Error.Detail}" : "");
        return result.Value;
    }

    /// <summary>The gateway is up within about 175 ms of start; allow a few seconds.</summary>
    private static async Task<HttpResponseMessage> GetWhenUpAsync(HttpClient http, Uri address)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var response = await http.GetAsync(address);
            if (response.StatusCode == HttpStatusCode.OK || DateTime.UtcNow > deadline)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(100);
        }
    }
}
