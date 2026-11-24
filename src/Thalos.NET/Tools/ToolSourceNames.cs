using Microsoft.Extensions.DependencyInjection;

namespace Thalos.Tools;

/// <summary>
/// The names of the tool sources added to one service collection through a registration that knows the name, so that no
/// two share one. A tool's qualified name is <c>{source}__{tool}</c>, so two sources of one name would expose the same
/// tool names and only one of them could be served; for a local source and a remote run tool source, the host could
/// serve a run's calls instead of the run's sandbox. <see cref="ThalosBuilder.AddLocalTools(string, Type[])"/>,
/// <see cref="ThalosBuilder.AddToolSource(IToolSource)"/> and Thalos.NET.Mcp record names here. Sources whose name is
/// only known once the container is built are checked by <see cref="ToolCatalog"/>.
/// </summary>
internal sealed class ToolSourceNames
{
    /// <summary>The kind of a local tool source, with its article, as the exception names it.</summary>
    public const string Local = "a local tool source";

    /// <summary>The kind of a plain or a run-scoped MCP entry that is not remote, as the exception names it.</summary>
    public const string Mcp = "an MCP server";

    /// <summary>The kind of a remote run tool source or a remote MCP entry, as the exception names it.</summary>
    public const string Remote = "a remote run tool source";

    /// <summary>The kind of a source added as an instance, as the exception names it.</summary>
    public const string Instance = "a tool source";

    private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);

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

    /// <summary>Records <paramref name="name"/> as the name of a source of <paramref name="kind"/>, after <see cref="ThrowIfTaken"/>.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="kind">The source's kind: <see cref="Local"/>, <see cref="Mcp"/>, <see cref="Remote"/> or <see cref="Instance"/>.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">A tool source of any kind already has the name.</exception>
    public void Add(string name, string kind, string paramName)
    {
        ThrowIfTaken(name, kind, paramName);
        _kinds.Add(name, kind);
    }

    /// <summary>Refuses <paramref name="name"/> for a source of <paramref name="kind"/> when a tool source of any kind already has it.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="kind">The new source's kind, as the exception names it.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">A tool source of any kind already has the name.</exception>
    public void ThrowIfTaken(string name, string kind, string paramName)
    {
        if (_kinds.TryGetValue(name, out var existing))
        {
            throw new ArgumentException(
                $"{char.ToUpperInvariant(existing[0])}{existing[1..]} named '{name}' was already added; {kind} cannot share its name, because tool source names must be unique.",
                paramName);
        }
    }
}
