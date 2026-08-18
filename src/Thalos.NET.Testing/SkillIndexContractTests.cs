using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Thalos.Skills;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="ISkillIndex"/> must satisfy — the suite Thalos runs against <c>InMemorySkillIndex</c>
/// and Daedalus runs against whatever it stores vectors in. Derive, implement
/// <see cref="CreateIndexAsync(IEmbeddingGenerator{string, Embedding{float}})"/> (a fresh, empty index over the given generator;
/// override <see cref="Dimensions"/> if your backend needs another size), let xUnit discover the inherited facts.
/// Every fact calls <c>CreateIndexAsync</c> exactly once, so an implementation may reset its backing table there.
/// </summary>
/// <remarks>
/// <para>
/// What the suite assumes beyond the interface docs: an exact query for a skill's own <see cref="ISkillIndex.EmbeddingText"/>
/// scores at or near 1 and outranks everything else; a blank or whitespace query returns an empty list rather than a failure;
/// <c>TopK</c> at or below zero behaves as 1; a name appears at most once; and removing an unknown name is a success.
/// </para>
/// <para>
/// Two facts are deliberately phrased against the grain. The blank-query fact asserts emptiness at <c>MinScore = 0</c>, not at
/// the default, because at any positive <c>MinScore</c> an index that happily embeds whitespace into a zero vector also returns
/// nothing — so the fact would pass without the short-circuit. And a query with no words at all is required to come <em>back</em>
/// at exactly <c>Score = 0</c> rather than to be absent: <c>NaN &gt;= x</c> is false for every <c>x</c>, so "returns empty" is
/// equally what an implementation with no zero-magnitude guard produces, and that implementation emits <c>NaN</c> scores against
/// a real embedding generator. A backend that delegates cosine to a database must therefore make a zero-magnitude vector score
/// zero, and must order ties by name.
/// </para>
/// </remarks>
public abstract class SkillIndexContractTests
{
    /// <summary>Vector size of the generator the suite builds — large enough that unrelated short texts do not collide (override when the backend is fixed to another size).</summary>
    protected virtual int Dimensions => 512;

    /// <summary>Creates a fresh, empty index over <paramref name="embeddings"/>.</summary>
    /// <param name="embeddings">The generator the index must embed skills and queries with.</param>
    protected abstract ValueTask<ISkillIndex> CreateIndexAsync(IEmbeddingGenerator<string, Embedding<float>> embeddings);

    /// <summary>Creates a fresh, empty index over a <see cref="HashedBagOfWordsEmbeddingGenerator"/> of <see cref="Dimensions"/>.</summary>
    protected ValueTask<ISkillIndex> CreateIndexAsync() => CreateIndexAsync(new HashedBagOfWordsEmbeddingGenerator(Dimensions));

    /// <summary>A valid document with the given name, description and tags.</summary>
    /// <param name="name">The skill name.</param>
    /// <param name="description">The one-line description that carries most of the signal.</param>
    /// <param name="tags">Optional tags, which are embedded too.</param>
    protected static SkillDocument Skill(string name, string description, params string[] tags) => new()
    {
        Name = SkillName.Parse(name),
        Description = description,
        Body = "# " + name + "\n1. Step.\n",
        Tags = tags,
        SourcePath = name + "/SKILL.md",
        ContentHash = new string('a', 64),
        UpdatedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task An_exact_query_finds_the_skill_first()
    {
        var index = await CreateIndexAsync();
        var release = Skill("release", "how we cut and publish a release");
        var upserted = await index.UpsertAsync([release, Skill("standup", "the daily standup format")], CancellationToken.None);
        upserted.IsSuccess.Should().BeTrue(upserted.IsFailure ? upserted.Error.ToString() : "");

        var hits = await index.SearchAsync(ISkillIndex.EmbeddingText(release), new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None);

        hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
        hits.Value.Should().NotBeEmpty();
        hits.Value[0].Name.Value.Should().Be("release");
        hits.Value[0].Score.Should().BeGreaterThan(0.9);
        hits.Value.Should().BeInDescendingOrder(h => h.Score);
    }

    /// <summary>
    /// <c>MinScore = 0</c> is the whole point: it admits a zero-scoring hit, so this only holds if a blank query
    /// short-circuits before the index is scanned. At the default <c>MinScore</c> it would pass either way.
    /// </summary>
    [Fact]
    public async Task A_blank_query_returns_an_empty_list_not_a_failure()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync([Skill("release", "how we cut a release")], CancellationToken.None);

        foreach (var query in new[] { "", "   ", "\t\n" })
        {
            var hits = await index.SearchAsync(query, new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None).ConfigureAwait(false);
            hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
            hits.Value.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A query made only of punctuation is not whitespace, so it passes the blank-query guard and reaches the scorer with a
    /// zero-magnitude vector. Demanding the hits <em>back</em> at exactly <c>Score = 0</c> is what makes this bite: an
    /// implementation that lets the division produce <c>NaN</c> returns an empty list here, because <c>NaN &gt;= 0</c> is false.
    /// Scoring every candidate identically also pins the documented tie-break, which is by name ascending.
    /// </summary>
    [Fact]
    public async Task A_wordless_query_scores_zero_rather_than_NaN_and_ties_break_by_name()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync([Skill("zeta", "the last one"), Skill("mid", "the middle one"), Skill("alpha", "the first one")], CancellationToken.None);

        var hits = await index.SearchAsync("!!! ???", new SkillSearchOptions { TopK = 10, MinScore = 0 }, CancellationToken.None);

        hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
        hits.Value.Should().OnlyContain(h => h.Score == 0d, "a zero-magnitude query vector must score 0, not NaN");
        hits.Value.Select(h => h.Name.Value).Should().Equal(["alpha", "mid", "zeta"], "equal scores are broken by name, so the TopK boundary is deterministic");

        (await index.SearchAsync("!!! ???", new SkillSearchOptions { TopK = 10, MinScore = 0.1 }, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_batch_is_a_success_and_changes_nothing()
    {
        var index = await CreateIndexAsync();
        (await index.UpsertAsync([], CancellationToken.None)).IsSuccess.Should().BeTrue();

        var hits = await index.SearchAsync("anything at all", new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None);
        hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
        hits.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_names_in_one_batch_are_last_wins()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync([Skill("release", "aardvark bassoon"), Skill("release", "cutting publishing tagging")], CancellationToken.None);

        var hits = await index.SearchAsync("cutting publishing tagging", new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None);
        hits.Value.Should().ContainSingle("one name is one vector");
        hits.Value[0].Score.Should().BeGreaterThan(0.5);

        (await index.SearchAsync("aardvark bassoon", new SkillSearchOptions { TopK = 5, MinScore = 0.5 }, CancellationToken.None)).Value.Should().BeEmpty("the earlier vector was replaced, not kept alongside");
    }

    [Fact]
    public async Task TopK_caps_the_result_and_a_value_at_or_below_zero_means_one()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync(
        [
            Skill("alpha", "shared word one"),
            Skill("beta", "shared word two"),
            Skill("gamma", "shared word three"),
        ], CancellationToken.None);

        (await index.SearchAsync("shared word", new SkillSearchOptions { TopK = 2, MinScore = 0 }, CancellationToken.None)).Value.Should().HaveCount(2);
        (await index.SearchAsync("shared word", new SkillSearchOptions { TopK = 0, MinScore = 0 }, CancellationToken.None)).Value.Should().ContainSingle();
        (await index.SearchAsync("shared word", new SkillSearchOptions { TopK = -5, MinScore = 0 }, CancellationToken.None)).Value.Should().ContainSingle();

        var all = (await index.SearchAsync("shared word", new SkillSearchOptions { TopK = 10, MinScore = 0 }, CancellationToken.None)).Value;
        all.Should().HaveCount(3, "MinScore 0 admits every vector, so nothing may be dropped from a three-row table");
        all.Select(h => h.Name.Value).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task MinScore_filters_and_Remove_drops_the_vector()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync([Skill("release", "how we cut a release")], CancellationToken.None);

        (await index.SearchAsync("entirely different subject matter", new SkillSearchOptions { TopK = 5, MinScore = 0.9 }, CancellationToken.None)).Value.Should().BeEmpty();

        (await index.RemoveAsync(SkillName.Parse("release"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await index.SearchAsync("how we cut a release", new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None)).Value.Should().BeEmpty("the only vector is gone, so even MinScore 0 has nothing to return");
        (await index.RemoveAsync(SkillName.Parse("release"), CancellationToken.None)).IsSuccess.Should().BeTrue("removing an unknown name is a success");
    }
}
