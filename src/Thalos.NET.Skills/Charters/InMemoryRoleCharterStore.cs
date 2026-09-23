using ZeroAlloc.Results;

namespace Thalos.Skills.Charters;

/// <summary>Non-durable store for tests, samples and single-process hosts. <paramref name="clock"/> stamps deactivations.</summary>
/// <param name="clock">Supplies the <c>UpdatedAt</c> stamp written by <see cref="DeactivateMissingAsync"/>.</param>
public sealed class InMemoryRoleCharterStore(TimeProvider clock) : IRoleCharterStore
{
    private readonly Dictionary<(string Role, string Hash), RoleCharter> _versions = new();
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal); // role -> content hash of its current version
    private readonly HashSet<string> _activeRoles = new(StringComparer.Ordinal);
    private readonly object _gate = new(); // Upsert and DeactivateMissing are both read-modify-write over the whole set

    /// <inheritdoc />
    public ValueTask<Result<RoleCharter, AgentError>> UpsertAsync(RoleCharter charter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(charter);
        lock (_gate)
        {
            _versions[(charter.Role, charter.ContentHash)] = charter;
            _current[charter.Role] = charter.ContentHash;
            _activeRoles.Add(charter.Role);
        }

        return new(Result<RoleCharter, AgentError>.Success(charter));
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<RoleCharter>, AgentError>> ListVersionsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RoleCharter> all = _versions.Values
                .Select(version => version with
                {
                    IsActive = _current.TryGetValue(version.Role, out var currentHash)
                        && string.Equals(currentHash, version.ContentHash, StringComparison.Ordinal)
                        && _activeRoles.Contains(version.Role),
                })
                .ToList();

            return new(Result<IReadOnlyList<RoleCharter>, AgentError>.Success(all));
        }
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<string> seenRoles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seenRoles);
        var keep = new HashSet<string>(seenRoles, StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var toDeactivate = new List<string>();
            foreach (var role in _activeRoles)
            {
                if (!keep.Contains(role))
                {
                    toDeactivate.Add(role);
                }
            }

            foreach (var role in toDeactivate)
            {
                _activeRoles.Remove(role);
                if (_current.TryGetValue(role, out var hash) && _versions.TryGetValue((role, hash), out var current))
                {
                    _versions[(role, hash)] = current with { UpdatedAt = now };
                }
            }
        }

        return new(UnitResult<AgentError>.Success());
    }
}
