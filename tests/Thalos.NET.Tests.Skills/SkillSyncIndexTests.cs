using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;
using Thalos.Testing;
using ZeroAlloc.Results;

namespace Thalos.Tests.Skills;

/// <summary>
/// Delegates to a real index and records the batches the sync asked it to embed and the names it asked it to drop, so
/// "the index is refilled on every start-up" and "nothing is embedded when no root is readable" are assertions rather
/// than inferences from search results. A hook returning an <see cref="AgentError"/> makes that call fail instead
/// (null = pass through), which is how "an index failure never fails host start" is proven. Attempts are recorded either way.
/// </summary>
internal sealed class RecordingSkillIndex(ISkillIndex inner) : ISkillIndex
{
    public List<IReadOnlyList<string>> UpsertBatches { get; } = [];

    public List<string> Removals { get; } = [];

    public Func<IReadOnlyList<SkillDocument>, AgentError?>? OnUpsert { get; set; }

    public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<SkillDocument> skills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var names = new List<string>(skills.Count);
        for (var i = 0; i < skills.Count; i++)
        {
            names.Add(skills[i].Name.Value);
        }

        UpsertBatches.Add(names);
        return OnUpsert?.Invoke(skills) is { } error ? new(UnitResult<AgentError>.Failure(error)) : inner.UpsertAsync(skills, ct);
    }

    /// <summary>The options object each search was handed, so a caller that mutates the bound singleton is visible.</summary>
    public List<SkillSearchOptions> Searches { get; } = [];

    public Func<string, AgentError?>? OnSearch { get; set; }

    public ValueTask<Result<IReadOnlyList<SkillHit>, AgentError>> SearchAsync(string query, SkillSearchOptions options, CancellationToken ct)
    {
        Searches.Add(options);
        return OnSearch?.Invoke(query) is { } error
            ? new(Result<IReadOnlyList<SkillHit>, AgentError>.Failure(error))
            : inner.SearchAsync(query, options, ct);
    }

    public ValueTask<UnitResult<AgentError>> RemoveAsync(SkillName name, CancellationToken ct)
    {
        Removals.Add(name.Value);
        return inner.RemoveAsync(name, ct);
    }
}

public sealed class SkillSyncIndexTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    private static SkillOptions Roots(params string[] roots)
    {
        var o = new SkillOptions();
        foreach (var root in roots)
        {
            o.Roots.Add(root);
        }

        return o;
    }

    /// <summary>
    /// The index is a rebuildable cache that does not survive the process, while the store does. If the content-hash skip
    /// governed the index too, a restart over an unmodified repository would leave every vector missing and
    /// <c>skills__search</c> would answer nothing. So the skip stays a <em>store</em> optimisation only.
    /// </summary>
    [Fact]
    public async Task Every_active_skill_is_indexed_even_when_its_file_did_not_change()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "how we cut and publish a release");
        var clock = Clock();
        var store = new RecordingSkillStore(new InMemorySkillStore(clock));
        var options = Roots(folder.Root);

        // the first sync fills a store that survives the process; the index of that process does not
        await new SkillSyncService(store, UnavailableSkillIndex.Instance, new SkillCatalogue(), Options.Create(options), clock).SyncAsync(CancellationToken.None);
        store.Upserts.Should().Equal(["release"]);

        var index = new RecordingSkillIndex(new InMemorySkillIndex(new HashedBagOfWordsEmbeddingGenerator(512)));
        var second = await new SkillSyncService(store, index, new SkillCatalogue(), Options.Create(options), clock).SyncAsync(CancellationToken.None);

        second.Value.Unchanged.Should().Be(1, "the file did not change, so the store upsert is skipped");
        store.Upserts.Should().Equal(["release"], "the hash skip still spares the store on the second sync");
        index.UpsertBatches.Should().ContainSingle().Which.Should().Equal(["release"], "the index is a cache and must be refilled on every start-up, in one batch");
        var hits = await index.SearchAsync("how we cut and publish a release", new SkillSearchOptions { TopK = 5, MinScore = 0.5 }, CancellationToken.None);
        hits.Value.Should().ContainSingle(h => h.Name.Value == "release");
    }

    [Fact]
    public async Task A_deactivated_skill_loses_its_vector()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "how we cut and publish a release");
        folder.WriteFlatSkill("notes", "house notes about the codebase");
        var clock = Clock();
        var index = new RecordingSkillIndex(new InMemorySkillIndex(new HashedBagOfWordsEmbeddingGenerator(512)));
        var sync = new SkillSyncService(new InMemorySkillStore(clock), index, new SkillCatalogue(), Options.Create(Roots(folder.Root)), clock);
        await sync.SyncAsync(CancellationToken.None);

        folder.Delete("notes.md");
        await sync.SyncAsync(CancellationToken.None);

        index.Removals.Should().Equal(["notes"], "only the skill that disappeared loses its vector");
        (await index.SearchAsync("house notes about the codebase", new SkillSearchOptions { TopK = 5, MinScore = 0.5 }, CancellationToken.None)).Value.Should().BeEmpty();
        (await index.SearchAsync("how we cut and publish a release", new SkillSearchOptions { TopK = 5, MinScore = 0.5 }, CancellationToken.None)).Value.Should().ContainSingle();
    }

    /// <summary>
    /// Skills must never depend on an embedding backend being up: the catalogue in the agent's instructions is
    /// authoritative and only <c>skills__search</c> degrades, so a failing index is a warning, not a failed host start.
    /// </summary>
    [Fact]
    public async Task An_index_failure_is_logged_and_never_fails_the_sync_or_the_host_start()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var clock = Clock();
        var log = new CapturingLogger<SkillSyncService>();
        var options = Roots(folder.Root);
        var index = new RecordingSkillIndex(UnavailableSkillIndex.Instance) { OnUpsert = _ => AgentError.SkillSearchUnavailable("no embeddings today") };
        var store = new RecordingSkillStore(new InMemorySkillStore(clock));
        var sync = new SkillSyncService(store, index, new SkillCatalogue(), Options.Create(options), clock, log);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the catalogue is authoritative; only search degrades");
        result.Value.Upserted.Should().Be(1, "the store was written even though the index was not");
        log.Entries.Should().Contain(e => e.EventId == 563 && e.Level == LogLevel.Warning);

        using var host = new HostBuilder().ConfigureServices(services =>
            services.AddSingleton<IHostedService>(new SkillSyncService(new InMemorySkillStore(Clock()), index, new SkillCatalogue(), Options.Create(options), Clock(), log))).Build();
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task When_no_root_can_be_read_the_index_is_left_alone()
    {
        var clock = Clock();
        var missing = Path.Combine(Path.GetTempPath(), "thalos-skills-missing-" + Guid.NewGuid().ToString("N"));
        var store = new InMemorySkillStore(clock);
        var index = new RecordingSkillIndex(new InMemorySkillIndex(new HashedBagOfWordsEmbeddingGenerator(512)));
        await store.UpsertAsync(SkillModelTests.Doc("planted", "how we cut and publish a release"), CancellationToken.None);
        await index.UpsertAsync([SkillModelTests.Doc("planted", "how we cut and publish a release")], CancellationToken.None);
        index.UpsertBatches.Clear();

        var result = await new SkillSyncService(store, index, new SkillCatalogue(), Options.Create(Roots(missing)), clock).SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        index.UpsertBatches.Should().BeEmpty();
        index.Removals.Should().BeEmpty("a path typo must never empty the search index either");
        (await index.SearchAsync("how we cut and publish a release", new SkillSearchOptions { TopK = 5, MinScore = 0.5 }, CancellationToken.None)).Value.Should().ContainSingle();
    }
}
