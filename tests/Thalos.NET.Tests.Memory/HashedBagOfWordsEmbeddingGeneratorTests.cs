using Microsoft.Extensions.AI;
using Thalos.Memory;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

public sealed class HashedBagOfWordsEmbeddingGeneratorTests
{
    [Fact]
    public async Task Vectors_are_deterministic_unit_length_and_reflect_word_overlap()
    {
        var g = new HashedBagOfWordsEmbeddingGenerator(64);
        var e = await g.GenerateAsync(["The user prefers xUnit over NUnit.", "the user PREFERS xunit over nunit", "Playwright locators use data-testid", "xUnit is preferred by the user"]);

        e.Should().HaveCount(4);
        e[0].Vector.ToArray().Should().Equal(e[1].Vector.ToArray(), "tokenisation is case-insensitive and punctuation-free");
        InMemoryMemoryIndex.Cosine(e[0].Vector.Span, e[0].Vector.Span).Should().BeApproximately(1.0, 1e-6);
        InMemoryMemoryIndex.Cosine(e[0].Vector.Span, e[2].Vector.Span).Should().BeApproximately(0.0, 1e-6, "no shared words");
        InMemoryMemoryIndex.Cosine(e[0].Vector.Span, e[3].Vector.Span).Should().BeInRange(0.3, 0.95);
        e[0].Vector.Length.Should().Be(64);
        g.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelDimensions.Should().Be(64);
        (await g.GenerateVectorAsync("")).ToArray().Should().OnlyContain(x => x == 0f, "empty text is the zero vector");
    }
}
