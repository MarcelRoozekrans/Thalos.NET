using Microsoft.Extensions.AI;

namespace Thalos.Testing;

/// <summary>
/// Deterministic, dependency-free <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> for tests: lower-cases, splits on
/// non-alphanumerics, hashes each token (FNV-1a) into <see cref="Dimensions"/> buckets and L2-normalises, so cosine similarity
/// approximates word overlap — identical texts score 1, disjoint texts 0 unless two tokens hash into the same bucket (with 128
/// buckets a long text overlaps a little with almost anything; keep test texts short or raise the dimensions). Reports
/// <see cref="EmbeddingGeneratorMetadata"/> with <c>DefaultModelDimensions</c> so dimension checks can be exercised. Not a semantic model.
/// </summary>
public sealed class HashedBagOfWordsEmbeddingGenerator(int dimensions = 128) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new("thalos-test-bow", null, "hashed-bag-of-words", dimensions);

    /// <summary>Vector length.</summary>
    public int Dimensions { get; } = dimensions > 0 ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions));

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new Embedding<float>(Embed(value)));
        }

        return Task.FromResult(result);
    }

    /// <summary>Embeds one text (public so tests can compare vectors directly).</summary>
    public float[] Embed(string? text)
    {
        var vector = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isToken = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isToken && start < 0)
            {
                start = i;
            }
            else if (!isToken && start >= 0)
            {
                vector[(int)(Fnv1a(text.AsSpan(start, i - start)) % (uint)Dimensions)] += 1f;
                start = -1;
            }
        }

        var norm = 0d;
        foreach (var x in vector)
        {
            norm += x * x;
        }

        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }

    private static uint Fnv1a(ReadOnlySpan<char> token)
    {
        var hash = 2166136261u;
        foreach (var c in token)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        return hash;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
