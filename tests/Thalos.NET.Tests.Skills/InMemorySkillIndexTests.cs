using Microsoft.Extensions.AI;
using Thalos.Skills;
using Thalos.Testing;

namespace Thalos.Tests.Skills;

public sealed class InMemorySkillIndexTests
{
    private static SkillDocument Doc(string name, string description, params string[] tags) =>
        SkillModelTests.Doc(name, description, tags: tags);

    private static InMemorySkillIndex NewIndex() => new(new HashedBagOfWordsEmbeddingGenerator(512));

    [Fact]
    public async Task Search_ranks_by_description_overlap_and_respects_TopK()
    {
        var index = NewIndex();
        await index.UpsertAsync(
        [
            Doc("release", "how we cut and publish a release"),
            Doc("migrations", "how to add and apply a database migration"),
            Doc("standup", "the daily standup format"),
        ], CancellationToken.None);

        // MinScore 0 admits every indexed vector, so TopK is the only thing that can shorten the list.
        var all = await index.SearchAsync("how do we publish a release", new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None);
        all.IsSuccess.Should().BeTrue(all.IsFailure ? all.Error.ToString() : "");
        all.Value.Should().HaveCount(3);

        var hits = await index.SearchAsync("how do we publish a release", new SkillSearchOptions { TopK = 2, MinScore = 0 }, CancellationToken.None);

        hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
        hits.Value.Should().HaveCount(2, "TopK trims the ranked list");
        hits.Value[0].Name.Value.Should().Be("release");
        hits.Value[0].Score.Should().BeGreaterThan(0.1);
        hits.Value[0].Score.Should().BeGreaterThan(hits.Value[1].Score * 1.5, "the release description is a far better match than the runner-up, not a near-tie");
    }

    [Fact]
    public async Task The_name_and_the_tags_contribute_to_the_match()
    {
        var index = NewIndex();
        await index.UpsertAsync([Doc("migrations", "adding a schema change", "efcore", "postgres")], CancellationToken.None);

        var byTag = await index.SearchAsync("efcore postgres", new SkillSearchOptions { TopK = 5, MinScore = 0.1 }, CancellationToken.None);
        byTag.Value.Should().ContainSingle(h => h.Name.Value == "migrations");

        var byName = await index.SearchAsync("migrations", new SkillSearchOptions { TopK = 5, MinScore = 0.1 }, CancellationToken.None);
        byName.Value.Should().ContainSingle(h => h.Name.Value == "migrations");
    }

    [Fact]
    public async Task The_body_is_never_embedded()
    {
        var index = NewIndex();
        var doc = SkillModelTests.Doc("release", "how we cut a release", body: "# Releasing\nRun the quokka wombat script.\n", tags: ["ci"]);
        ISkillIndex.EmbeddingText(doc).Should().Contain("release").And.Contain("how we cut a release").And.Contain("ci").And.NotContain("quokka");

        await index.UpsertAsync([doc], CancellationToken.None);

        (await index.SearchAsync("quokka wombat script", new SkillSearchOptions { TopK = 5, MinScore = 0.1 }, CancellationToken.None))
            .Value.Should().BeEmpty("skills are embedded from name, description and tags — never the body");
    }

    [Fact]
    public async Task A_blank_query_returns_nothing_and_MinScore_filters()
    {
        var index = NewIndex();
        await index.UpsertAsync([Doc("release", "how we cut a release")], CancellationToken.None);

        (await index.SearchAsync("   ", new SkillSearchOptions(), CancellationToken.None)).Value.Should().BeEmpty();

        // MinScore 0 would admit a zero-scoring hit, so this only holds if the blank query short-circuits
        // before the index is scanned at all — with the default MinScore the assertion passes either way.
        (await index.SearchAsync("   ", new SkillSearchOptions { MinScore = 0 }, CancellationToken.None)).Value.Should().BeEmpty();

        (await index.SearchAsync("completely unrelated words", new SkillSearchOptions { MinScore = 0.9 }, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_is_last_wins_and_Remove_drops_the_vector()
    {
        var index = NewIndex();
        await index.UpsertAsync([Doc("release", "aaaa bbbb"), Doc("release", "cutting and publishing")], CancellationToken.None);
        (await index.SearchAsync("cutting and publishing", new SkillSearchOptions { MinScore = 0.5 }, CancellationToken.None)).Value.Should().ContainSingle();

        (await index.RemoveAsync(SkillName.Parse("release"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await index.SearchAsync("cutting and publishing", new SkillSearchOptions { MinScore = 0.5 }, CancellationToken.None)).Value.Should().BeEmpty();
        (await index.RemoveAsync(SkillName.Parse("never-there"), CancellationToken.None)).IsSuccess.Should().BeTrue("removing an unknown name is not an error");
    }

    [Fact]
    public async Task An_empty_batch_and_an_empty_index_are_successful_and_empty()
    {
        var index = NewIndex();
        (await index.UpsertAsync([], CancellationToken.None)).IsSuccess.Should().BeTrue();

        var hits = await index.SearchAsync("anything at all", new SkillSearchOptions(), CancellationToken.None);
        hits.IsSuccess.Should().BeTrue();
        hits.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_with_no_words_scores_zero_rather_than_NaN()
    {
        var index = NewIndex();
        await index.UpsertAsync([Doc("release", "how we cut a release")], CancellationToken.None);

        // "!!! ???" is not whitespace, so it passes the blank guard and reaches a zero-magnitude vector.
        // MinScore 0 is deliberate: NaN fails every comparison, so a NaN score would silently drop the hit
        // and an "is empty" assertion would pass for the wrong reason. Demanding the hit back at exactly 0 bites.
        var scored = await index.SearchAsync("!!! ???", new SkillSearchOptions { TopK = 5, MinScore = 0 }, CancellationToken.None);
        scored.IsSuccess.Should().BeTrue();
        scored.Value.Should().ContainSingle().Which.Score.Should().Be(0d, "a zero-magnitude query vector must score 0, not NaN");

        var filtered = await index.SearchAsync("!!! ???", new SkillSearchOptions { TopK = 5, MinScore = 0.1 }, CancellationToken.None);
        filtered.Value.Should().BeEmpty();
    }

    [Fact]
    public void Cosine_handles_zero_magnitude_mismatched_and_empty_vectors()
    {
        InMemorySkillIndex.Cosine([3f, 4f], [3f, 4f]).Should().BeApproximately(1d, 1e-9);
        InMemorySkillIndex.Cosine([0f, 0f], [1f, 0f]).Should().Be(0d);
        InMemorySkillIndex.Cosine([1f, 0f], [0f, 0f]).Should().Be(0d);
        InMemorySkillIndex.Cosine([1f, 0f, 0f], [1f, 0f]).Should().Be(0d);
        InMemorySkillIndex.Cosine([], []).Should().Be(0d);
        double.IsNaN(InMemorySkillIndex.Cosine([0f, 0f], [0f, 0f])).Should().BeFalse();
    }

    [Fact]
    public async Task The_bound_search_options_are_read_never_mutated()
    {
        var index = NewIndex();
        await index.UpsertAsync(
        [
            Doc("release", "how we cut and publish a release"),
            Doc("migrations", "how to apply a database migration"),
        ], CancellationToken.None);

        var options = new SkillSearchOptions { TopK = 0, MinScore = 0.05 };
        var hits = await index.SearchAsync("how do we publish a release", options, CancellationToken.None);

        hits.Value.Should().ContainSingle("a TopK of 0 is treated as 1");
        options.TopK.Should().Be(0, "SkillSearchOptions is a bound singleton and must not be normalised in place");
        options.MinScore.Should().Be(0.05);
    }

    [Fact]
    public async Task Generator_failure_maps_to_SkillSearchUnavailable_without_exception_text()
    {
        var index = new InMemorySkillIndex(new ThrowingGenerator());

        var upsert = await index.UpsertAsync([Doc("release", "how we cut a release")], CancellationToken.None);
        upsert.IsFailure.Should().BeTrue();
        upsert.Error.Code.Should().Be(AgentErrorCode.SkillSearchUnavailable);
        upsert.Error.Detail.Should().Be(nameof(HttpRequestException));
        upsert.Error.ToString().Should().NotContain("connection refused");

        var search = await index.SearchAsync("anything", new SkillSearchOptions(), CancellationToken.None);
        search.IsFailure.Should().BeTrue();
        search.Error.Code.Should().Be(AgentErrorCode.SkillSearchUnavailable);
        search.Error.Detail.Should().Be(nameof(HttpRequestException));
    }

    [Fact]
    public async Task The_unavailable_index_says_so_on_search_and_no_ops_everything_else()
    {
        var index = UnavailableSkillIndex.Instance;
        (await index.UpsertAsync([Doc("release", "x")], CancellationToken.None)).IsSuccess.Should().BeTrue("indexing must not fail a host that has no embedding generator");
        (await index.RemoveAsync(SkillName.Parse("release"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var search = await index.SearchAsync("anything", new SkillSearchOptions(), CancellationToken.None);
        search.IsFailure.Should().BeTrue();
        search.Error.Code.Should().Be(AgentErrorCode.SkillSearchUnavailable);
        search.Error.Message.Should().Be(UnavailableSkillIndex.Reason);
    }

    private sealed class ThrowingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new HttpRequestException("connection refused");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
