using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;
using ZeroAlloc.Results;

namespace Thalos.Tests.Skills;

/// <summary>
/// Delegates to a real store and records what the sync actually asked it to do, so "an unchanged file is not
/// upserted at all" and "the sweep is told every loaded name" are assertions rather than inferences from state.
/// </summary>
internal sealed class RecordingSkillStore(ISkillStore inner) : ISkillStore
{
    public List<string> Upserts { get; } = [];

    public List<IReadOnlyList<string>> DeactivateCalls { get; } = [];

    public ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        Upserts.Add(skill.Name.Value);
        return inner.UpsertAsync(skill, ct);
    }

    public ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct) => inner.GetAsync(name, ct);

    public ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct) => inner.ListAsync(query, ct);

    public ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var names = new List<string>(seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            names.Add(seen[i].Value);
        }

        DeactivateCalls.Add(names);
        return inner.DeactivateMissingAsync(seen, ct);
    }
}

public sealed class SkillSyncServiceTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    private static (SkillSyncService Sync, RecordingSkillStore Store) Build(TimeProvider clock, params string[] roots)
    {
        var options = new SkillOptions();
        foreach (var root in roots)
        {
            options.Roots.Add(root);
        }

        var store = new RecordingSkillStore(new InMemorySkillStore(clock));
        return (new SkillSyncService(store, Options.Create(options), clock), store);
    }

    [Fact]
    public async Task A_first_sync_loads_every_file_and_reports_what_it_did()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        folder.WriteFlatSkill("notes", "House notes.", tags: "[house, notes]");
        var clock = Clock();
        var (sync, store) = Build(clock, folder.Root);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        result.Value.Should().Be(new SkillSyncReport(Scanned: 2, Upserted: 2, Unchanged: 0, Skipped: 0, Deactivated: 0));
        var stored = (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value;
        stored.Select(s => s.Name.Value).Should().Equal(["notes", "release"]);
        stored.Single(s => s.Name == SkillName.Parse("notes")).Tags.Should().Equal(["house", "notes"]);
        stored.Single(s => s.Name == SkillName.Parse("release")).UpdatedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task An_unchanged_file_is_skipped_by_its_hash_and_keeps_its_timestamp()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var clock = Clock();
        var (sync, store) = Build(clock, folder.Root);
        await sync.SyncAsync(CancellationToken.None);
        var firstWrite = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromHours(1));
        var second = await sync.SyncAsync(CancellationToken.None);

        second.Value.Should().Be(new SkillSyncReport(1, 0, 1, 0, 0));
        store.Upserts.Should().Equal(["release"], "the second sync must not touch the store at all when the hash is unchanged");
        (await store.GetAsync(SkillName.Parse("release"), CancellationToken.None)).Value.UpdatedAt.Should().Be(firstWrite);
    }

    [Fact]
    public async Task An_edited_file_is_upserted_again()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "Old words.");
        var clock = Clock();
        var (sync, store) = Build(clock, folder.Root);
        await sync.SyncAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1));
        folder.WriteFolderSkill("release", "New words.");
        var second = await sync.SyncAsync(CancellationToken.None);

        second.Value.Should().Be(new SkillSyncReport(1, 1, 0, 0, 0));
        store.Upserts.Should().Equal(["release", "release"]);
        var stored = (await store.GetAsync(SkillName.Parse("release"), CancellationToken.None)).Value;
        stored.Description.Should().Be("New words.");
        stored.UpdatedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task A_deleted_file_deactivates_its_skill_without_losing_the_row()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        folder.WriteFlatSkill("notes");
        var clock = Clock();
        var (sync, store) = Build(clock, folder.Root);
        await sync.SyncAsync(CancellationToken.None);

        folder.Delete("notes.md");
        var second = await sync.SyncAsync(CancellationToken.None);

        second.Value.Should().Be(new SkillSyncReport(1, 0, 1, 0, 1));
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["release"]);
        (await store.GetAsync(SkillName.Parse("notes"), CancellationToken.None)).Value.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task The_sweep_is_told_every_name_that_loaded()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        folder.WriteFlatSkill("notes");
        var (sync, store) = Build(Clock(), folder.Root);

        await sync.SyncAsync(CancellationToken.None);

        store.DeactivateCalls.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(["notes", "release"], "an incomplete seen-list would deactivate live skills");
    }

    [Fact]
    public async Task Several_roots_are_scanned_in_order()
    {
        using var shared = new SkillFolder("shared");
        using var repo = new SkillFolder("repo");
        shared.WriteFolderSkill("release");
        repo.WriteFolderSkill("migrations");
        var (sync, store) = Build(Clock(), shared.Root, repo.Root);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.Value.Scanned.Should().Be(2);
        store.Upserts.Should().Equal(["release", "migrations"], "roots are scanned in configuration order, which is what makes 'first root wins' deterministic");
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["migrations", "release"]);
    }

    [Fact]
    public async Task No_roots_configured_is_a_no_op_that_never_deactivates_anything()
    {
        var clock = Clock();
        var (sync, store) = Build(clock);
        await store.UpsertAsync(SkillModelTests.Doc("planted"), CancellationToken.None);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.Value.Should().Be(new SkillSyncReport(0, 0, 0, 0, 0));
        store.DeactivateCalls.Should().BeEmpty();
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }
}
