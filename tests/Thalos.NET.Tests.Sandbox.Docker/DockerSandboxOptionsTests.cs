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

    [Fact]
    public void The_defaults_are_valid() => new DockerSandboxOptions().Validate().IsSuccess.Should().BeTrue();
}
