using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;

namespace Thalos.Tests.Skills;

/// <summary>
/// Which skills an agent sees, and the per-glob-set cache that makes a turn a dictionary lookup. The cache is a correctness
/// boundary before it is a performance one: two agents with different glob sets must never share an entry, or one agent's
/// catalogue leaks into the other's prompt.
/// </summary>
public sealed class SkillCatalogueGlobTests
{
    private static SkillCatalogue Loaded()
    {
        var catalogue = new SkillCatalogue();
        catalogue.Set(
        [
            SkillModelTests.Doc("release", "How we cut a release."),
            SkillModelTests.Doc("dotnet-migrations", "How to add a migration."),
            SkillModelTests.Doc("dotnet-testing", "How we write tests."),
        ], maxChars: 2000);
        return catalogue;
    }

    private static string[] Listed(string? block) =>
        block is null ? [] : [.. block.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..l.IndexOf(':', StringComparison.Ordinal)])];

    [Theory]
    [InlineData(new[] { "*" }, new[] { "dotnet-migrations", "dotnet-testing", "release" })]
    [InlineData(new[] { "dotnet-*" }, new[] { "dotnet-migrations", "dotnet-testing" })]
    [InlineData(new[] { "release", "dotnet-testing" }, new[] { "dotnet-testing", "release" })]
    [InlineData(new[] { "dotnet-?esting" }, new[] { "dotnet-testing" })]
    [InlineData(new[] { "*-testing" }, new[] { "dotnet-testing" })]
    [InlineData(new[] { "dotnet*" }, new[] { "dotnet-migrations", "dotnet-testing" })]
    public void Globs_select_skills_ordinally_and_case_sensitively(string[] globs, string[] expected) =>
        Listed(Loaded().Render(globs)).Should().Equal(expected, "the block lists exactly the matching skills, sorted by name");

    // The empty-match branch: an agent that opted in but whose globs select nothing gets nothing at all, not an empty block.
    [Theory]
    [InlineData("Release")]  // Glob is ordinal and case-sensitive
    [InlineData("RELEASE")]
    [InlineData("nothing-*")]
    [InlineData("release?")] // '?' is exactly one char, never zero
    [InlineData("")]         // an empty glob matches only an empty name, which no skill has
    [InlineData("dotnet-")]  // no implicit prefix matching
    public void A_glob_set_that_matches_nothing_renders_no_block_at_all(string glob) =>
        Loaded().Render([glob]).Should().BeNull("an empty <skills></skills> block would cost tokens and say nothing");

    [Fact]
    public void An_empty_glob_list_renders_nothing()
    {
        Loaded().Render([]).Should().BeNull();

        // Not delegated to Glob: the filter itself denies an empty list, because AgentDefinition.Skills defaults to empty
        // and skills are opt-in. Tools default to ["*"]; skills deliberately do not.
        SkillCatalogue.IsAllowed([], "release").Should().BeFalse("no glob means no skill, never every skill");
        SkillCatalogue.Matching([SkillModelTests.Doc("release")], []).Should().BeEmpty();
    }

    [Fact]
    public void The_same_glob_set_is_rendered_once_and_Set_invalidates_the_cache()
    {
        var catalogue = Loaded();
        var first = catalogue.Render(["dotnet-*"]);
        catalogue.Render(["dotnet-*"]).Should().BeSameAs(first, "a turn costs a dictionary lookup, not a render");
        catalogue.Render(["dotnet-*", "release"]).Should().NotBeSameAs(first, "a different glob set is a different entry");

        catalogue.Set([SkillModelTests.Doc("dotnet-migrations", "Rewritten.")], maxChars: 2000);
        catalogue.Render(["dotnet-*"]).Should().NotBeSameAs(first).And.Contain("Rewritten.");
    }

    [Fact]
    public void One_agents_catalogue_never_leaks_into_another_agents()
    {
        var catalogue = Loaded();

        // Render the wider set first: a colliding key would hand the narrower agent the wider agent's skills.
        catalogue.Render(["*"]).Should().NotBeNull();
        Listed(catalogue.Render(["release"])).Should().Equal("release");
        Listed(catalogue.Render(["dotnet-*"])).Should().Equal("dotnet-migrations", "dotnet-testing");
        catalogue.Render(["nothing-*"]).Should().BeNull("a cached wider render must not answer a narrower query");

        // And in the other direction: a narrow entry cached first must not narrow the next agent.
        var fresh = Loaded();
        fresh.Render(["release"]).Should().NotBeNull();
        Listed(fresh.Render(["*"])).Should().Equal("dotnet-migrations", "dotnet-testing", "release");
    }

    [Fact]
    public void Glob_order_is_a_separate_cache_entry_but_never_a_different_block()
    {
        var catalogue = Loaded();
        var forward = catalogue.Render(["release", "dotnet-testing"]);
        var reversed = catalogue.Render(["dotnet-testing", "release"]);

        reversed.Should().Be(forward, "entries are ordered by skill name, so the order of the globs cannot show through");
        reversed.Should().NotBeSameAs(forward, "the key is the globs joined in order, so a reordered set renders a second time");
    }

    [Fact]
    public async Task The_sync_publishes_the_active_set_to_the_catalogue()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        folder.WriteFlatSkill("notes", "House notes.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var options = new SkillOptions { Catalogue = { MaxChars = 500 } };
        options.Roots.Add(folder.Root);
        var catalogue = new SkillCatalogue();
        var sync = new SkillSyncService(new InMemorySkillStore(clock), UnavailableSkillIndex.Instance, catalogue, Options.Create(options), clock);

        catalogue.Render(["*"]).Should().BeNull("nothing has been synced yet");
        await sync.SyncAsync(CancellationToken.None);

        catalogue.Render(["*"]).Should().Contain("- release: How we cut a release.").And.Contain("- notes: House notes.");

        folder.Delete("notes.md");
        await sync.SyncAsync(CancellationToken.None);
        catalogue.Render(["*"]).Should().NotContain("- notes:", "a deactivated skill leaves the catalogue");
    }

    [Fact]
    public async Task The_sync_hands_the_catalogue_the_configured_budget()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        folder.WriteFlatSkill("notes", "House notes.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var options = new SkillOptions { Catalogue = { MaxChars = 90 } };
        options.Roots.Add(folder.Root);
        var catalogue = new SkillCatalogue();
        var sync = new SkillSyncService(new InMemorySkillStore(clock), UnavailableSkillIndex.Instance, catalogue, Options.Create(options), clock);

        await sync.SyncAsync(CancellationToken.None);

        // A budget too small for even one entry is the documented floor: no entry, an honest count, and no silent truncation.
        var block = catalogue.Render(["*"])!;
        Listed(block).Should().BeEmpty();
        block.Should().Contain("… and 2 more (use skills__search)", "Thalos:Skills:Catalogue:MaxChars is what the sync publishes, not a default");
    }

    [Fact]
    public async Task An_edited_file_reaches_the_catalogue_on_the_next_sync()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var options = new SkillOptions();
        options.Roots.Add(folder.Root);
        var catalogue = new SkillCatalogue();
        var sync = new SkillSyncService(new InMemorySkillStore(clock), UnavailableSkillIndex.Instance, catalogue, Options.Create(options), clock);

        await sync.SyncAsync(CancellationToken.None);
        catalogue.Render(["*"]).Should().Contain("How we cut a release.");

        folder.WriteFolderSkill("release", "How we cut a release, rewritten.");
        await sync.SyncAsync(CancellationToken.None);

        catalogue.Render(["*"]).Should().Contain("- release: How we cut a release, rewritten.")
            .And.NotContain("- release: How we cut a release.\n", "a stale cache entry would hide the edit until a restart");
    }

    [Fact]
    public async Task A_failing_index_still_leaves_the_catalogue_published()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var options = new SkillOptions();
        options.Roots.Add(folder.Root);
        var catalogue = new SkillCatalogue();
        var index = new RecordingSkillIndex(UnavailableSkillIndex.Instance) { OnUpsert = _ => AgentError.SkillSearchUnavailable("no embeddings today") };
        var sync = new SkillSyncService(new InMemorySkillStore(clock), index, catalogue, Options.Create(options), clock);

        await sync.SyncAsync(CancellationToken.None);

        index.UpsertBatches.Should().ContainSingle("the index was asked and refused");
        catalogue.Render(["*"]).Should().Contain("- release: How we cut a release.", "the catalogue is published before the index upsert, so a broken backend cannot blank it");
    }
    /// <summary>
    /// <c>AgentDefinition.Skills</c> is an unvalidated list, so a glob may contain the cache key's separator
    /// or be empty. With a merely-separated key, ["release", "dotnet-testing"] and the single glob
    /// "release\u001Fdotnet-testing" produced the same key and shared one cache entry — one agent's catalogue
    /// served to another, or (if the forged set rendered first) a real agent silently losing its own.
    /// </summary>
    [Fact]
    public void A_glob_containing_the_key_separator_cannot_forge_another_glob_sets_cache_entry()
    {
        var catalogue = Loaded();

        var real = catalogue.Render(["release", "dotnet-testing"]);
        var forged = catalogue.Render(["release\u001Fdotnet-testing"]);
        var emptyThenReal = catalogue.Render(["", "release"]);
        var forgedEmpty = catalogue.Render(["\u001Frelease"]);

        Listed(real).Should().Equal(["dotnet-testing", "release"]);
        forged.Should().BeNull("one glob containing a separator matches no skill name");
        Listed(emptyThenReal).Should().Equal(["release"]);
        forgedEmpty.Should().BeNull("no skill name contains a control character");
    }

}
