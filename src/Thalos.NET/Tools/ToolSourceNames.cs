using Microsoft.Extensions.DependencyInjection;

namespace Thalos.Tools;

/// <summary>
/// The names of the local and the remote run tool sources added to one service collection, so that a name is never both.
/// The tool catalog keeps the first source of a name, so a local source sharing a remote source's name could serve a
/// run's calls on the host instead of in the run's sandbox. <see cref="ThalosBuilder.AddLocalTools"/> records local names;
/// Thalos.NET.Mcp records remote ones.
/// </summary>
internal sealed class ToolSourceNames
{
    private readonly HashSet<string> _local = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remote = new(StringComparer.Ordinal);

    /// <summary>The collection's names, created and registered on first use.</summary>
    /// <param name="services">The service collection.</param>
    public static ToolSourceNames Of(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(ToolSourceNames))?.ImplementationInstance is ToolSourceNames existing)
        {
            return existing;
        }

        var created = new ToolSourceNames();
        services.AddSingleton(created);
        return created;
    }

    /// <summary>Records <paramref name="name"/> as a local source's name.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">A remote run tool source has the name.</exception>
    public void AddLocal(string name, string paramName)
    {
        if (_remote.Contains(name))
        {
            throw new ArgumentException(
                $"A remote run tool source named '{name}' was already added; a local tool source cannot share its name.", paramName);
        }

        _local.Add(name);
    }

    /// <summary>Records <paramref name="name"/> as a remote run tool source's name.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">A local tool source or another remote run tool source has the name.</exception>
    public void AddRemote(string name, string paramName)
    {
        if (_local.Contains(name))
        {
            throw new ArgumentException(
                $"A local tool source named '{name}' was already added; a remote run tool source cannot share its name.", paramName);
        }

        if (!_remote.Add(name))
        {
            throw new ArgumentException($"A remote run tool source named '{name}' was already added.", paramName);
        }
    }
}
