using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>Brute-force cosine index over the injected embedding generator; for tests, samples and small hosts.</summary>
public sealed class InMemoryMemoryIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings) : IMemoryIndex
{
    private sealed record Entry(string OwnerId, AgentId? AgentId, float[] Vector);

    private readonly ConcurrentDictionary<MemoryId, Entry> _entries = new();

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        try
        {
            var vectors = await embeddings.GenerateAsync(records.Select(r => r.Text), null, ct).ConfigureAwait(false);
            if (vectors.Count != records.Count)
            {
                return UnitResult<AgentError>.Failure(AgentError.MemoryIndexFailed("The embedding generator returned a different number of vectors than texts."));
            }

            for (var i = 0; i < records.Count; i++)
            {
                _entries[records[i].Id] = new Entry(records[i].OwnerId, records[i].AgentId, vectors[i].Vector.ToArray());
            }

            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(AgentError.MemoryIndexUnavailable("The embedding generator failed.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(scope.OwnerId))
        {
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Success([]);
        }

        try
        {
            var vector = await embeddings.GenerateVectorAsync(query, null, ct).ConfigureAwait(false);
            var hits = new List<MemoryHit>();
            foreach (var (id, entry) in _entries)
            {
                if (!scope.Includes(entry.OwnerId, entry.AgentId))
                {
                    continue;
                }

                var score = Cosine(vector.Span, entry.Vector);
                if (score >= options.MinScore)
                {
                    hits.Add(new MemoryHit(id, score));
                }
            }

            hits.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            IReadOnlyList<MemoryHit> top = hits.Count > options.TopK ? hits.GetRange(0, Math.Max(0, options.TopK)) : hits;
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Success(top);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Failure(AgentError.MemoryIndexUnavailable("The embedding generator failed.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct)
    {
        _entries.TryRemove(id, out _);
        return new(UnitResult<AgentError>.Success());
    }

    /// <inheritdoc />
    public ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct) =>
        new(Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, embeddings.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelDimensions)));

    /// <summary>Cosine similarity; 0 when either vector is zero or lengths differ.</summary>
    internal static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
