using Thalos.Sandbox.Docker;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>No engine needed: config rendering and option validation.</summary>
public sealed class DockerSandboxOptionsTests
{
    private const char NewLine = (char)10;
    private const char CarriageReturn = (char)13;
    private static readonly string[] Subnet = ["172.19.0.0/16"];

    /// <summary>
    /// Squid refuses a domain list in which one entry covers another, so each of these would stop the proxy, and an IP
    /// literal is never a domain. Red: drop the overlap check, the IP-literal check, or the regex anchor \z, which lets a
    /// newline inject a directive.
    /// </summary>
    [Theory]
    [InlineData("nuget.org")]
    [InlineData("foo.nuget.org")]
    [InlineData(".nuget.org")]
    [InlineData(".api.nuget.org")]
    [InlineData("Example.com")]
    [InlineData("example")]
    [InlineData("example.com\nhttp_access allow all")]
    [InlineData("example.com\n")]
    [InlineData("example.com extra.org")]
    [InlineData("1.2.3.4")]
    [InlineData(".10.0.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("::1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("2001:db8::1")]
    public void A_bad_extra_domain_is_refused(string domain)
    {
        var options = new DockerSandboxOptions();
        options.ExtraEgressDomains.Add(domain);

        options.Validate().IsFailure.Should().BeTrue();
        DockerSandboxInfrastructure.RenderSquidConf(options.ExtraEgressDomains, Subnet).IsFailure.Should().BeTrue();
    }

    /// <summary>Red: drop the check against earlier extra entries.</summary>
    [Theory]
    [InlineData(".example.com", "a.example.com")]
    [InlineData("a.example.com", ".example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    public void Overlapping_extra_domains_are_refused(string first, string second) =>
        DockerSandboxOptions.ValidateEgressDomains([first, second]).IsFailure.Should().BeTrue();

    /// <summary>Red: make the overlap check too eager, for example treat any shared suffix as overlap.</summary>
    [Fact]
    public void Disjoint_extra_domains_are_rendered_into_the_acl()
    {
        var squid = DockerSandboxInfrastructure.RenderSquidConf(["example.com", "a.example.com", ".pkgs.example.org"], Subnet);

        squid.IsSuccess.Should().BeTrue();
        squid.Value.Split('\n').Should().Contain("acl nuget dstdomain -n .nuget.org example.com a.example.com .pkgs.example.org");
    }

    /// <summary>
    /// Pins squid.conf: the spike's working config, plus <c>-n</c> so an IP-literal destination is never matched by its
    /// reverse DNS name, a client ACL limited to the internal network's subnets, and Safe_ports. Red: drop <c>-n</c>,
    /// the src ACL or Safe_ports, put api.nuget.org back next to .nuget.org, or log to stdio:/dev/stdout.
    /// </summary>
    [Fact]
    public void Squid_conf_is_pinned()
    {
        var squid = DockerSandboxInfrastructure.RenderSquidConf([], ["172.19.0.0/16", "fd00:1::/64"]).Value;

        squid.Should().NotContain(CarriageReturn.ToString());
        squid.Split(NewLine, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).Should().Equal(
            "http_port 3128",
            "pinger_enable off",
            "acl sandboxes src 172.19.0.0/16 fd00:1::/64",
            "acl nuget dstdomain -n .nuget.org",
            "acl SSL_ports port 443",
            "acl Safe_ports port 80",
            "acl Safe_ports port 443",
            "acl CONNECT method CONNECT",
            "http_access deny !sandboxes",
            "http_access deny !Safe_ports",
            "http_access deny CONNECT !SSL_ports",
            "http_access allow CONNECT nuget",
            "http_access allow nuget",
            "http_access deny all",
            "cache deny all",
            "access_log daemon:/var/log/squid/access.log squid");
    }

    /// <summary>Red: render squid.conf without the network's subnets, which would leave the proxy open to every bridge container.</summary>
    [Theory]
    [InlineData]
    [InlineData("not-a-subnet")]
    public void Squid_conf_needs_a_valid_client_subnet(params string[] subnets) =>
        DockerSandboxInfrastructure.RenderSquidConf([], subnets).IsFailure.Should().BeTrue();

    /// <summary>Red: take whole seconds only, or drop the fraction padding.</summary>
    [Theory]
    [InlineData("2026-10-01T22:44:02.123456789Z", "1790894642.123456789")]
    [InlineData("2026-10-01T22:44:02.5Z", "1790894642.500000000")]
    [InlineData("2026-10-01T22:44:02Z", "1790894642.000000000")]
    [InlineData("0001-01-01T00:00:00Z", "-62135596800.000000000")]
    [InlineData("garbage", null)]
    public void Since_is_the_start_in_seconds_and_nanoseconds(string startedAt, string? expected) =>
        DockerSandboxInfrastructure.SinceOf(startedAt).Should().Be(expected);

    /// <summary>Red: unquote the location regex, which nginx refuses at startup.</summary>
    [Fact]
    public void Nginx_conf_quotes_its_regexes()
    {
        var nginx = DockerSandboxInfrastructure.NginxConf;

        nginx.Should().Contain("location ~ \"^/sandboxes/(?<sid>[0-9a-f]{32})/\" {");
        nginx.Should().Contain("\"~^/sandboxes/[0-9a-f]{32}(?<r>/[^?]*)\" $r;");
        nginx.Should().Contain("proxy_pass http://thalos-sandbox-$sid:8080$sbx_rest$is_args$args;");
        nginx.Should().Contain("if ($sbx_raw_sid != $sid) { return 404; }");
    }

    /// <summary>Red: remove the matching check in Validate, one per case.</summary>
    [Theory]
    [InlineData("network")]
    [InlineData("same-names")]
    [InlineData("port")]
    [InlineData("timeout")]
    public void Bad_options_are_refused(string what)
    {
        var options = new DockerSandboxOptions();
        switch (what)
        {
            case "network":
                options.InternalNetwork = "bad name";
                break;
            case "same-names":
                options.EgressContainerName = options.GatewayContainerName;
                break;
            case "port":
                options.GatewayPort = 70000;
                break;
            default:
                options.EngineTimeout = TimeSpan.Zero;
                break;
        }

        options.Validate().IsFailure.Should().BeTrue();
    }

    /// <summary>
    /// A client timeout of one engine call is not the whole set-up budget. Red: map every non-caller cancellation to the
    /// budget message.
    /// </summary>
    [Fact]
    public void A_set_up_cancellation_names_what_ran_out()
    {
        DockerSandboxInfrastructure.SetUpCancelled(budgetExpired: true, TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(15))
            .Message.Should().Be("could not set up the sandbox network within 900 s");
        DockerSandboxInfrastructure.SetUpCancelled(budgetExpired: false, TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(15))
            .Message.Should().Be("could not set up the sandbox network: a Docker engine call timed out after 15 s");
    }

    /// <summary>
    /// Through SetUpAsync's real catch: an engine that accepts the connection and never answers makes the first engine
    /// call hit the client's own timeout while the set-up budget is far from spent, so the error names the engine call.
    /// Red: at SetUpAsync's call site, pass true for budgetExpired; the error then names the whole budget.
    /// </summary>
    [Fact]
    public async Task A_silent_engine_fails_set_up_as_an_engine_call_timeout_not_the_budget()
    {
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var accepted = new List<System.Net.Sockets.TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await silent.AcceptTcpClientAsync());
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                // the listener stopped
            }
        });
        var options = new DockerSandboxOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{((System.Net.IPEndPoint)silent.LocalEndpoint).Port}"),
            EngineTimeout = TimeSpan.FromSeconds(1),
        };
        using var docker = new global::Docker.DotNet.DockerClientBuilder().WithEndpoint(options.Endpoint).WithTimeout(options.EngineTimeout).Build();
        using var infrastructure = new DockerSandboxInfrastructure(docker, options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var result = await infrastructure.EnsureAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        silent.Stop();
        await accepting;
        accepted.ForEach(c => c.Dispose());
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Be("could not set up the sandbox network: a Docker engine call timed out after 1 s");
    }

    /// <summary>
    /// A Windows-container engine cannot run the Linux-only images, so set-up refuses it before any container, network
    /// or pull call. Red: drop the engine OS check from <c>EnsureLinuxEngineAsync</c>.
    /// </summary>
    [Fact]
    public async Task A_windows_container_engine_is_refused_before_anything_is_created()
    {
        await using var engine = new WindowsEngineStub();
        using var docker = new global::Docker.DotNet.DockerClientBuilder().WithEndpoint(engine.Options.Endpoint!).WithTimeout(engine.Options.EngineTimeout).Build();
        using var infrastructure = new DockerSandboxInfrastructure(docker, engine.Options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var result = await infrastructure.EnsureAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Be("run sandboxes need a Linux container engine; this engine runs windows containers");
        engine.Paths.Should().OnlyContain(p => p.EndsWith("/info", StringComparison.Ordinal));
    }

    /// <summary>
    /// Get and list go through the same check: they answer null and empty, and ask the engine nothing but its info.
    /// Red, verified separately: make <c>ListAsync</c> ignore a failed <c>EnsureAsync</c>, which sends /containers/json, then <c>GetAsync</c>, which sends the container inspect.
    /// </summary>
    [Fact]
    public async Task Listing_and_getting_on_a_windows_container_engine_find_nothing_and_ask_only_for_info()
    {
        await using var engine = new WindowsEngineStub();
        await using var runtime = new DockerSandboxRuntime(engine.Options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DockerSandboxRuntime>.Instance);

        var listed = await runtime.ListAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var got = await runtime.GetAsync(new string('a', 32), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        listed.Should().BeEmpty();
        got.Should().BeNull();
        engine.Paths.Should().NotBeEmpty().And.OnlyContain(p => p.EndsWith("/info", StringComparison.Ordinal));
    }

    /// <summary>A local HTTP engine that answers every call as a Windows-container engine and records the paths asked.</summary>
    private sealed class WindowsEngineStub : IAsyncDisposable
    {
        private readonly System.Net.HttpListener listener = new();
        private readonly Task serving;
        private readonly List<string> paths = [];

        public WindowsEngineStub()
        {
            var probe = System.Net.Sockets.TcpListener.Create(0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Options = new DockerSandboxOptions { Endpoint = new Uri($"http://127.0.0.1:{port}"), EngineTimeout = TimeSpan.FromSeconds(5) };
            serving = Task.Run(ServeAsync);
        }

        public DockerSandboxOptions Options { get; }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (paths)
                {
                    return [.. paths];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            await serving;
            listener.Close();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync();
                    lock (paths)
                    {
                        paths.Add(context.Request.Url!.AbsolutePath);
                    }

                    var body = System.Text.Encoding.UTF8.GetBytes("{\"OSType\":\"windows\"}");
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
            {
                // the listener stopped
            }
        }
    }

    /// <summary>Red: never remove an entry, or release the gate without leaving.</summary>
    [Fact]
    public async Task A_keyed_lock_serialises_one_key_and_forgets_it_afterwards()
    {
        var keyed = new KeyedLock();
        var first = await keyed.TryAcquireAsync("a", TimeSpan.FromSeconds(1), CancellationToken.None);
        first.Should().NotBeNull();

        (await keyed.TryAcquireAsync("a", TimeSpan.FromMilliseconds(50), CancellationToken.None)).Should().BeNull();
        using (var other = await keyed.TryAcquireAsync("b", TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            other.Should().NotBeNull();
        }

        first!.Dispose();
        using (var again = await keyed.TryAcquireAsync("a", TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            again.Should().NotBeNull();
        }

        keyed.Count.Should().Be(0);
    }

    [Fact]
    public void The_defaults_are_valid() => new DockerSandboxOptions().Validate().IsSuccess.Should().BeTrue();
}
