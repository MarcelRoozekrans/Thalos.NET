using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;
using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class CharterSyncServiceTests
{
    private static readonly AgentId ReviewerId = AgentId.New();

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    private static AgentEnvelope Envelope(string role, IReadOnlyList<string>? tools = null) => new()
    {
        Id = ReviewerId,
        Name = role,
        Tools = tools ?? ["*"],
    };

    private static string ValidCharter(string role, string instructions, string? model = null) =>
        "---\nname: " + role + "\ndescription: A role.\n" + (model is null ? "" : "model: " + model + "\n") + "---\n\n" + instructions + "\n";

    private static AgentDefinition Get(CharteredAgentCatalog catalog, AgentId id)
    {
        catalog.TryGet(id, out var definition).Should().BeTrue();
        return definition!;
    }

    private static (CharterSyncService Sync, InMemoryRoleCharterStore Store, CharteredAgentCatalog Catalog) Build(string root, params AgentEnvelope[] envelopes) =>
        Build(root, Clock(), envelopes);

    private static (CharterSyncService Sync, InMemoryRoleCharterStore Store, CharteredAgentCatalog Catalog) Build(
        string root, TimeProvider clock, params AgentEnvelope[] envelopes)
    {
        var options = new CharterOptions();
        options.Roots.Add(root);
        foreach (var envelope in envelopes)
        {
            options.Envelopes.Add(envelope);
        }

        var store = new InMemoryRoleCharterStore(clock);
        var catalog = new CharteredAgentCatalog(Options.Create(new ThalosOptions()), Options.Create(options));
        var sync = new CharterSyncService(store, catalog, Options.Create(options), clock);
        return (sync, store, catalog);
    }

    [Fact]
    public async Task A_charter_file_that_names_tools_is_skipped_and_the_previous_version_stays_active()
    {
        using var folder = new SkillFolder();
        folder.WriteRaw("reviewer.md", ValidCharter("reviewer", "Old."));
        var (sync, store, catalog) = Build(folder.Root, Envelope("reviewer"));
        (await sync.SyncAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();

        folder.WriteRaw("reviewer.md", ValidCharter("reviewer", "New.").Replace("description:", "tools: [git__push]\ndescription:", StringComparison.Ordinal));
        var report = await sync.SyncAsync(CancellationToken.None);

        report.IsSuccess.Should().BeTrue(report.IsFailure ? report.Error.ToString() : "");
        report.Value.Skipped.Should().Be(1);
        var d = Get(catalog, ReviewerId);
        d.Instructions.Should().Be("Old.", "a refused file must not deactivate or replace the version already live");
        (await store.ListVersionsAsync(CancellationToken.None)).Value.Single(v => v.IsActive).Instructions.Should().Be("Old.");
    }

    [Fact]
    public async Task An_envelope_without_a_charter_fails_sync_naming_the_role()
    {
        using var folder = new SkillFolder();
        var (sync, _, _) = Build(folder.Root, Envelope("reviewer"));

        var report = await sync.SyncAsync(CancellationToken.None);

        report.IsFailure.Should().BeTrue();
        report.Error.Message.Should().Contain("reviewer");
    }

    [Fact]
    public async Task A_charter_in_a_role_folder_is_loaded_the_same_as_a_flat_file()
    {
        using var folder = new SkillFolder();
        folder.WriteRaw("reviewer/CHARTER.md", ValidCharter("reviewer", "Folder form."));
        var (sync, _, catalog) = Build(folder.Root, Envelope("reviewer"));

        var report = await sync.SyncAsync(CancellationToken.None);

        report.IsSuccess.Should().BeTrue(report.IsFailure ? report.Error.ToString() : "");
        report.Value.Should().Be(new SkillSyncReport(1, 1, 0, 0, 0));
        var d = Get(catalog, ReviewerId);
        d.Instructions.Should().Be("Folder form.");
    }

    [Fact]
    public async Task A_second_sync_with_an_unchanged_file_does_not_touch_the_store()
    {
        using var folder = new SkillFolder();
        folder.WriteRaw("reviewer.md", ValidCharter("reviewer", "Same."));
        var clock = Clock();
        var (sync, _, _) = Build(folder.Root, clock, Envelope("reviewer"));
        await sync.SyncAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1));
        var second = await sync.SyncAsync(CancellationToken.None);

        second.IsSuccess.Should().BeTrue(second.IsFailure ? second.Error.ToString() : "");
        second.Value.Should().Be(new SkillSyncReport(1, 0, 1, 0, 0));
    }

    [Fact]
    public async Task Deleting_the_only_charter_file_deactivates_the_role_and_the_next_sync_fails_the_envelope()
    {
        using var folder = new SkillFolder();
        folder.WriteRaw("reviewer.md", ValidCharter("reviewer", "Here."));
        var (sync, _, catalog) = Build(folder.Root, Envelope("reviewer"));
        (await sync.SyncAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();
        catalog.TryGet(ReviewerId, out _).Should().BeTrue();

        folder.Delete("reviewer.md");
        var report = await sync.SyncAsync(CancellationToken.None);

        report.IsFailure.Should().BeTrue();
        report.Error.Message.Should().Contain("reviewer");
        catalog.TryGet(ReviewerId, out _).Should().BeFalse("the deactivated role must not still be served as an agent");
    }
}
