using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// In-memory <see cref="IProcessDefinitionStore"/> for the unit-level workflow tests: no Postgres, no Docker.
/// </summary>
/// <remarks>
/// Deliberately stores the <em>YAML</em> and parses it on read, exactly as
/// <c>Thalos.Workflow.Orm.OrmProcessDefinitionStore</c> does, rather than holding the already-parsed
/// <see cref="ProcessDefinition"/> the writer happened to pass. Holding the parsed object would let a test pass
/// while the real store's read path — a SELECT plus a <see cref="ProcessLoader.Load"/> — was broken, which is the
/// half of resolution these tests exist to cover. <see cref="GetCallCount"/> is here so a caching test can prove
/// the decorator actually stops calls reaching this far rather than merely returning the right answer twice.
/// </remarks>
internal sealed class InMemoryProcessDefinitionStore : IProcessDefinitionStore
{
    private readonly Dictionary<(string Process, int Version), string> _yaml = [];
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

    /// <summary>Every definition <see cref="UpsertAndActivateAsync"/> was called with, in call order.</summary>
    public List<ProcessDefinition> Activated { get; } = [];

    /// <summary>How many times <see cref="GetAsync"/> has been called, cache decorators included or not.</summary>
    public int GetCallCount { get; private set; }

    /// <summary>
    /// When set, <see cref="TryRemoveAsync"/> refuses — standing in for the real store's "a run still pins this
    /// version" rejection, so a test can distinguish a removal that happened from one that was turned down.
    /// </summary>
    public bool RefuseRemoval { get; set; }

    public ValueTask<Result> UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
    {
        // Mirrors the real store's immutability rule: same content at the same version is an idempotent success
        // that still activates; different content at the same version is refused with nothing written. A fake that
        // kept the old overwrite-always behaviour would let the tests pass while the property the store now
        // enforces was broken.
        if (_yaml.TryGetValue((definition.Name, definition.Version), out var stored) && !string.Equals(stored, yaml, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(Result.Failure(
                $"Process '{definition.Name}' version {definition.Version} is already stored with different content. A stored version is immutable, because a run that started on it must keep the exact graph it started on — bump the version instead of editing version {definition.Version} in place."));
        }

        Activated.Add(definition);
        _yaml[(definition.Name, definition.Version)] = yaml;
        _active[definition.Name] = definition.Version;
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct) =>
        ValueTask.FromResult(_active.TryGetValue(process, out var version) ? version : (int?)null);

    public ValueTask<Result<ProcessDefinition>> GetAsync(string process, int version, CancellationToken ct)
    {
        GetCallCount++;

        // Same two failure shapes, and the same messages' shape, as the real store: nothing stored for the pair,
        // and a stored row that no longer parses. Both name the process and version.
        if (!_yaml.TryGetValue((process, version), out var yaml))
        {
            return ValueTask.FromResult(Result<ProcessDefinition>.Failure(
                $"No process definition stored for '{process}' version {version}."));
        }

        var loaded = ProcessLoader.Load(yaml);
        return ValueTask.FromResult(loaded.IsSuccess
            ? loaded
            : Result<ProcessDefinition>.Failure(
                $"The stored definition for '{process}' version {version} could not be parsed: {loaded.Error}"));
    }

    public ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct)
    {
        if (RefuseRemoval)
        {
            return ValueTask.FromResult(Result.Failure($"Process '{process}' version {version} is still pinned by a run."));
        }

        _yaml.Remove((process, version));
        return ValueTask.FromResult(Result.Success());
    }

    /// <summary>
    /// Seeds <paramref name="yaml"/> directly, bypassing <see cref="UpsertAndActivateAsync"/>, for a test that
    /// wants a definition present without asserting anything about how it got there.
    /// </summary>
    public InMemoryProcessDefinitionStore Seed(string yaml)
    {
        var definition = ProcessLoader.Load(yaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        _yaml[(definition.Value.Name, definition.Value.Version)] = yaml;
        _active[definition.Value.Name] = definition.Value.Version;
        return this;
    }
}
