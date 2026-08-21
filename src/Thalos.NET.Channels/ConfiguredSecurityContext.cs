using ZeroAlloc.Authorization;

namespace Thalos.Channels;

/// <summary>
/// An <see cref="ISecurityContext"/> assembled from configuration rather than derived from an inbound request.
/// Every non-HTTP channel (console, Telegram, …) has to manufacture a caller identity for the pump; this is that
/// identity, shared by every such channel rather than redefined per-package.
/// </summary>
public sealed class ConfiguredSecurityContext : ISecurityContext
{
    /// <summary>Creates a caller identity from a configured <paramref name="id"/> and <paramref name="roles"/>.</summary>
    /// <param name="id">The caller id (e.g. <c>telegram:marcel</c>). Session ownership is keyed on this, so it cannot be blank.</param>
    /// <param name="roles">The caller's roles. Compared ordinally — see <see cref="Roles"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null, empty, or white space.</exception>
    public ConfiguredSecurityContext(string id, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id;
        Roles = roles.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>
    /// The configured roles, compared with <see cref="StringComparer.Ordinal"/>. A case-insensitive comparison would
    /// let a role such as <c>"Developer"</c> silently satisfy a policy written against the lower-case
    /// <c>"developer"</c>, so the comparison is deliberately case-sensitive.
    /// </summary>
    public IReadOnlySet<string> Roles { get; }

    /// <summary>Always empty: configured callers carry no claims, only an id and roles.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
