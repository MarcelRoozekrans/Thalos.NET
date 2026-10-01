using System.Text.RegularExpressions;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Docker;

/// <summary>Configuration for <see cref="DockerSandboxRuntime"/>.</summary>
public sealed partial class DockerSandboxOptions
{
    /// <summary>null = platform default (npipe://./pipe/docker_engine, unix:///var/run/docker.sock).</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// The internal network every sandbox is attached to. It also scopes this runtime: every object it creates carries
    /// a <c>thalos.sandbox.network</c> label with this name, and <see cref="ISandboxRuntime.ListAsync"/>,
    /// <see cref="ISandboxRuntime.GetAsync"/> and <see cref="ISandboxRuntime.DeleteAsync"/> only see sandboxes with the same label.
    /// </summary>
    public string InternalNetwork { get; set; } = "thalos-sandboxes";

    /// <summary>Daedalus pins these by digest; the defaults are tags for tests.</summary>
    public string GatewayImage { get; set; } = "nginx:1.27-alpine";

    /// <summary>The egress proxy image. Daedalus pins it by digest; the default is a tag for tests.</summary>
    public string EgressImage { get; set; } = "ubuntu/squid:6.6-24.04_beta";

    /// <summary>The gateway container's name.</summary>
    public string GatewayContainerName { get; set; } = "thalos-sandbox-gateway";

    /// <summary>The egress proxy container's name.</summary>
    public string EgressContainerName { get; set; } = "thalos-sandbox-egress";

    /// <summary>Loopback port the gateway publishes; 0 = pick a free one.</summary>
    public int GatewayPort { get; set; }

    /// <summary>Domains the egress proxy allows beyond NuGet. Empty by default; reviewed config only.</summary>
    /// <remarks>
    /// Each entry is a lowercase domain, optionally with a leading dot for "this domain and its subdomains". Squid refuses
    /// to start when one entry covers another, so an entry covered by <c>.nuget.org</c> or by another entry is rejected.
    /// </remarks>
    public IList<string> ExtraEgressDomains { get; } = [];

    /// <summary>How long one Docker Engine request may take. Kept short so a missing engine fails fast.</summary>
    public TimeSpan EngineTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a freshly started gateway or egress proxy may take to accept connections.</summary>
    public TimeSpan InfrastructureReadyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long pulling a missing gateway or egress image may take. Run images are never pulled.</summary>
    public TimeSpan ImagePullTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The domains the egress proxy always allows.</summary>
    internal static readonly string[] BuiltInEgressDomains = [".nuget.org"];

    /// <summary>Checks every option, including that no egress domain overlaps another.</summary>
    /// <returns>Success, or a validation error naming the offending option.</returns>
    public UnitResult<AgentError> Validate()
    {
        if (!DockerName().IsMatch(InternalNetwork ?? ""))
        {
            return Invalid($"InternalNetwork '{InternalNetwork}' is not a valid Docker network name.");
        }

        if (!DockerName().IsMatch(GatewayContainerName ?? "") || !DockerName().IsMatch(EgressContainerName ?? ""))
        {
            return Invalid($"GatewayContainerName '{GatewayContainerName}' and EgressContainerName '{EgressContainerName}' must be valid Docker container names.");
        }

        if (string.Equals(GatewayContainerName, EgressContainerName, StringComparison.Ordinal))
        {
            return Invalid("GatewayContainerName and EgressContainerName must differ.");
        }

        if (string.IsNullOrWhiteSpace(GatewayImage) || string.IsNullOrWhiteSpace(EgressImage))
        {
            return Invalid("GatewayImage and EgressImage must be set.");
        }

        if (GatewayPort is < 0 or > 65535)
        {
            return Invalid($"GatewayPort {GatewayPort} is outside 0-65535.");
        }

        if (EngineTimeout <= TimeSpan.Zero || InfrastructureReadyTimeout <= TimeSpan.Zero || ImagePullTimeout <= TimeSpan.Zero)
        {
            return Invalid("EngineTimeout, InfrastructureReadyTimeout and ImagePullTimeout must be positive.");
        }

        return ValidateEgressDomains(ExtraEgressDomains);
    }

    /// <summary>Validates extra egress domains against the pattern, the built-ins and each other.</summary>
    internal static UnitResult<AgentError> ValidateEgressDomains(IEnumerable<string> extra)
    {
        var accepted = new List<string>(BuiltInEgressDomains);
        foreach (var domain in extra)
        {
            if (domain is null || !EgressDomain().IsMatch(domain))
            {
                return Invalid($"Egress domain '{domain}' must be a lowercase domain, optionally with a leading dot.");
            }

            // An IP literal is matched by address, so it is never a domain; an all-digit last label is how one looks.
            if (IsAllDigits(domain.AsSpan(domain.LastIndexOf('.') + 1)))
            {
                return Invalid($"Egress domain '{domain}' is an IP address; only domain names may be allowed.");
            }

            foreach (var other in accepted)
            {
                if (Overlaps(domain, other))
                {
                    return Invalid($"Egress domain '{domain}' overlaps '{other}'; squid refuses a domain list in which one entry covers another.");
                }
            }

            accepted.Add(domain);
        }

        return UnitResult<AgentError>.Success();
    }

    /// <summary>Whether squid's dstdomain matching would treat one entry as covering the other.</summary>
    private static bool Overlaps(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || Covers(a, b) || Covers(b, a);

    /// <summary>A leading-dot entry covers its bare domain and every subdomain.</summary>
    private static bool Covers(string wide, string narrow)
    {
        if (!wide.StartsWith('.'))
        {
            return false;
        }

        var core = wide[1..];
        var other = narrow.TrimStart('.');
        return string.Equals(other, core, StringComparison.Ordinal) || other.EndsWith(wide, StringComparison.Ordinal);
    }

    private static bool IsAllDigits(ReadOnlySpan<char> label)
    {
        foreach (var c in label)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static UnitResult<AgentError> Invalid(string message) => UnitResult<AgentError>.Failure(AgentError.Validation(message));

    // \z, not $: in .NET, $ also matches before a trailing newline, which would let an entry inject a squid directive.
    [GeneratedRegex(@"^\.?[a-z0-9-]+(\.[a-z0-9-]+)+\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EgressDomain();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DockerName();
}
