using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// Brute-force cosine index over the injected embedding generator; a skill library is a folder of files, so an in-process
/// scan is the right size. Hits are ordered by score descending, then by name (deterministic).
/// </summary>
/// <param name="embeddings">The generator used to embed skills and queries.</param>
/// <remarks>The cosine helper is a deliberate copy of the one in Thalos.NET.Memory: the two packages must not depend on each other.</remarks>
public sealed class InMemorySkillIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings) : ISkillIndex
{
    private readonly ConcurrentDictionary<SkillName, float[]> _vectors = new();

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<SkillDocument> skills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (skills.Count == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        var texts = new string[skills.Count];
        for (var i = 0; i < skills.Count; i++)
        {
            texts[i] = ISkillIndex.EmbeddingText(skills[i]);
        }

        try
        {
            var vectors = await embeddings.GenerateAsync(texts, null, ct).ConfigureAwait(false);
            if (vectors.Count != skills.Count)
            {
                return UnitResult<AgentError>.Failure(AgentError.SkillSearchUnavailable("The embedding generator returned a different number of vectors than texts."));
            }

            for (var i = 0; i < skills.Count; i++)
            {
                _vectors[skills[i].Name] = vectors[i].Vector.ToArray();
            }

            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(AgentError.SkillSearchUnavailable("The embedding generator failed.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SkillHit>, AgentError>> SearchAsync(string query, SkillSearchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result<IReadOnlyList<SkillHit>, AgentError>.Success([]);
        }

        // options is a bound singleton (SkillOptions.Search): read it, never normalise it in place.
        var minScore = options.MinScore;
        var topK = Math.Max(1, options.TopK);

        try
        {
            var vector = await embeddings.GenerateVectorAsync(query, null, ct).ConfigureAwait(false);
            var hits = new List<SkillHit>();
            foreach (var (name, candidate) in _vectors)
            {
                var score = Cosine(vector.Span, candidate);
                if (score >= minScore)
                {
                    hits.Add(new SkillHit(name, score));
                }
            }

            hits.Sort(static (a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Name.CompareTo(b.Name);
            });

            IReadOnlyList<SkillHit> top = hits.Count > topK ? hits.GetRange(0, topK) : hits;
            return Result<IReadOnlyList<SkillHit>, AgentError>.Success(top);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<SkillHit>, AgentError>.Failure(AgentError.SkillSearchUnavailable("The embedding generator failed.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> RemoveAsync(SkillName name, CancellationToken ct)
    {
        _vectors.TryRemove(name, out _);
        return new(UnitResult<AgentError>.Success());
    }

    /// <summary>Cosine similarity; 0 when either vector is zero-magnitude, when the lengths differ, or when they are empty (never NaN).</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>A similarity in [-1, 1]; in practice [0, 1] for non-negative embeddings.</returns>
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
