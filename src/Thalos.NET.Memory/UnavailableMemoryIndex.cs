using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>Default index when no <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> is registered: remember stores with <c>IndexPending</c>, recall finds nothing, probe says so.</summary>
public sealed class UnavailableMemoryIndex : IMemoryIndex
{
    /// <summary>The probe detail / error message explaining how to get an index.</summary>
    public const string Reason = "No memory index is configured: register an IEmbeddingGenerator<string, Embedding<float>> (in-memory index) or call UseRagNetMemory(...).";

    /// <summary>The singleton (the type is stateless).</summary>
    public static UnavailableMemoryIndex Instance { get; } = new();

    private UnavailableMemoryIndex()
    {
    }

    private static AgentError Error => AgentError.MemoryIndexUnavailable(Reason);

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct) => new(UnitResult<AgentError>.Failure(Error));

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct) => new(Result<IReadOnlyList<MemoryHit>, AgentError>.Failure(Error));

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct) => new(UnitResult<AgentError>.Success());

    /// <inheritdoc />
    public ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct) => new(Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(false, null, Reason)));
}
