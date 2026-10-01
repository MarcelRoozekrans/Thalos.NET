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
    /// PidsLimit, MemorySwap, NanoCPUs, Tmpfs, Mounts, NetworkMode and User. Memory: halve it, because the engine
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

        host.NetworkMode.Should().Be(fixture.Network);
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
    /// S1. Red: pass the process environment through, appending Environment.GetEnvironmentVariables() to Env. Leave
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

            var expected = spec.Environment(new Uri("http://egress:3128")).Keys.Union(image.Config!.Env!.Select(Key), StringComparer.Ordinal);
            container.Config!.Env!.Select(Key).Should().BeEquivalentTo(expected);
            container.Config.Env.Should().NotContain(e => e.StartsWith("GITHUB_TOKEN=", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }

        static string Key(string entry) => entry[..entry.IndexOf('=', StringComparison.Ordinal)];
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

    /// <summary>S2. Red: add example.com to the ACL in squid.conf.</summary>
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
    }

    /// <summary>Red: loosen the location regex to <c>(?&lt;sid&gt;[^/]+)</c>, or take the remainder from the decoded <c>$uri</c>.</summary>
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

    /// <summary>Red: skip the volume removal, or treat the second delete's 404 as a failure.</summary>
    [SkippableFact]
    public async Task Delete_is_idempotent_and_removes_the_volume()
    {
        Skip.IfNot(DockerAvailable.Value);
        var handle = await CreateAsync(DockerSandboxFixture.Spec(fixture.CurlImage));

        (await fixture.Runtime.DeleteAsync(handle.SandboxId, Ct)).IsSuccess.Should().BeTrue();

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
