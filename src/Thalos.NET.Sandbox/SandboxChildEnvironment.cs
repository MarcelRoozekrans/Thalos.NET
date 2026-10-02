using ModelContextProtocol.Client;

namespace Thalos.Sandbox;

/// <summary>
/// The environment every process a sandbox starts begins from: git, restore, build, test and the Roslyn server. Never the
/// sandbox host's own environment, which holds <see cref="SandboxEnvironment.Token"/>; those processes run code an
/// agent may have written.
/// </summary>
/// <remarks>
/// It holds ModelContextProtocol's curated operating-system defaults (<c>PATH</c>, <c>HOME</c> and the like), the
/// temporary-directory and locale variables, every <c>DOTNET_*</c> and <c>NUGET_*</c> variable, and the proxy and
/// certificate variables restore needs to reach the egress proxy. Nothing else, and never a <c>THALOS_*</c> variable.
/// </remarks>
public static class SandboxChildEnvironment
{
    private static readonly string[] Names =
    [
        "TMPDIR", "TMP", "TEMP", "LANG", "WINDIR", "COMSPEC", "PROGRAMDATA",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR",
    ];

    private static readonly string[] Prefixes = ["DOTNET_", "NUGET_"];

    /// <summary>The curated environment, read from this process now.</summary>
    public static IReadOnlyDictionary<string, string> Curated() => Build(Environment.GetEnvironmentVariables);

    /// <summary>The curated environment, read from <paramref name="read"/>.</summary>
    /// <param name="read">Returns the host's environment.</param>
    internal static IReadOnlyDictionary<string, string> Build(Func<System.Collections.IDictionary> read)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in StdioClientTransportOptions.GetDefaultEnvironmentVariables())
        {
            if (value is not null && !IsThalos(key))
            {
                environment[key] = value;
            }
        }

        foreach (System.Collections.DictionaryEntry entry in read())
        {
            if (entry.Key is string key && entry.Value is string value && !IsThalos(key)
                && (Array.Exists(Names, n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))
                    || Array.Exists(Prefixes, p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static bool IsThalos(string key) => key.StartsWith("THALOS_", StringComparison.OrdinalIgnoreCase);
}
