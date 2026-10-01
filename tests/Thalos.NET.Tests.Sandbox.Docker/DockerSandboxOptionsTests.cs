using Thalos.Sandbox.Docker;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>No engine needed: config rendering and option validation.</summary>
public sealed class DockerSandboxOptionsTests
{
    /// <summary>
    /// Squid refuses a domain list in which one entry covers another, so each of these would stop the proxy.
    /// Red: drop the overlap check, or the regex anchor \z, which lets a newline inject a directive.
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
    public void A_bad_extra_domain_is_refused(string domain)
    {
        var options = new DockerSandboxOptions();
        options.ExtraEgressDomains.Add(domain);

        options.Validate().IsFailure.Should().BeTrue();
        DockerSandboxInfrastructure.RenderSquidConf(options.ExtraEgressDomains).IsFailure.Should().BeTrue();
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
        var squid = DockerSandboxInfrastructure.RenderSquidConf(["example.com", "a.example.com", ".pkgs.example.org"]);

        squid.IsSuccess.Should().BeTrue();
        squid.Value.Split('\n').Should().Contain("acl nuget dstdomain .nuget.org example.com a.example.com .pkgs.example.org");
    }

    /// <summary>Pins the spike's working squid config. Red: put api.nuget.org back next to .nuget.org, or log to stdio:/dev/stdout.</summary>
    [Fact]
    public void Squid_conf_is_the_spikes_working_config()
    {
        var squid = DockerSandboxInfrastructure.RenderSquidConf([]).Value;

        squid.Should().NotContain("\r");
        squid.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).Should().Equal(
            "http_port 3128",
            "acl nuget dstdomain .nuget.org",
            "acl SSL_ports port 443",
            "acl CONNECT method CONNECT",
            "http_access deny CONNECT !SSL_ports",
            "http_access allow CONNECT nuget",
            "http_access allow nuget",
            "http_access deny all",
            "cache deny all",
            "access_log daemon:/var/log/squid/access.log squid");
    }

    /// <summary>Red: unquote the location regex, which nginx refuses at startup.</summary>
    [Fact]
    public void Nginx_conf_quotes_its_regexes()
    {
        var nginx = DockerSandboxInfrastructure.NginxConf;

        nginx.Should().Contain("location ~ \"^/sandboxes/(?<sid>[0-9a-f]{32})/\" {");
        nginx.Should().Contain("\"~^/sandboxes/[0-9a-f]{32}(?<r>/[^?]*)\" $r;");
        nginx.Should().Contain("proxy_pass http://thalos-sandbox-$sid:8080$sbx_rest$is_args$args;");
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
