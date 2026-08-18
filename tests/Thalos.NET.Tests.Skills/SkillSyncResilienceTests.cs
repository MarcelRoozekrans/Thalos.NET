using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;

namespace Thalos.Tests.Skills;

/// <summary>Records the event id, level and rendered message of every log call, so "logged, not fatal" is an assertion rather than a hope.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(int EventId, LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }
}

/// <summary>A second hosted service that reads the store the moment it starts, so "the sync runs first" is observable.</summary>
internal sealed class StoreProbe(ISkillStore store) : IHostedService
{
    public int SkillsVisibleAtStart { get; private set; } = -1;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        SkillsVisibleAtStart = (await store.ListAsync(new SkillQuery(), cancellationToken).ConfigureAwait(false)).Value.Count;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SkillSyncResilienceTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    private static (SkillSyncService Sync, RecordingSkillStore Store, CapturingLogger<SkillSyncService> Log) Build(TimeProvider clock, SkillOptions options)
    {
        var store = new RecordingSkillStore(new InMemorySkillStore(clock));
        var log = new CapturingLogger<SkillSyncService>();
        return (new SkillSyncService(store, UnavailableSkillIndex.Instance, new SkillCatalogue(), Options.Create(options), clock, log), store, log);
    }

    private static SkillOptions Roots(params string[] roots)
    {
        var o = new SkillOptions();
        foreach (var root in roots)
        {
            o.Roots.Add(root);
        }

        return o;
    }

    /// <summary>A real host wired the way production wires it: the sync is a hosted service, so host start runs <c>StartingAsync</c>.</summary>
    private static IHost BuildHost(ISkillStore store, SkillOptions options, TimeProvider clock, StoreProbe? probe = null) =>
        new HostBuilder().ConfigureServices(services =>
        {
            if (probe is not null)
            {
                // registered *before* the sync: only the lifecycle contract can make it see a populated store
                services.AddSingleton<IHostedService>(probe);
            }

            services.AddSingleton<IHostedService>(new SkillSyncService(store, UnavailableSkillIndex.Instance, new SkillCatalogue(), Options.Create(options), clock, new CapturingLogger<SkillSyncService>()));
        }).Build();

    [Fact]
    public async Task A_malformed_file_is_logged_and_skipped_while_the_good_ones_land()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("good");
        folder.WriteRaw("broken/SKILL.md", "no frontmatter here");
        folder.WriteRaw("mismatch/SKILL.md", "---\nname: elsewhere\ndescription: x\n---\nbody\n");
        var options = Roots(folder.Root);
        var (sync, store, log) = Build(Clock(), options);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        result.Value.Should().Be(new SkillSyncReport(1, 1, 0, 2, 0));
        log.Entries.Count(e => e.EventId == 561 && e.Level == LogLevel.Warning).Should().Be(2);
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["good"]);

        // the headline guarantee: one malformed skill must not stop a host, and the good ones must still be there
        var hosted = new RecordingSkillStore(new InMemorySkillStore(Clock()));
        using var host = BuildHost(hosted, options, Clock());
        await host.StartAsync(CancellationToken.None);
        (await hosted.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value)
            .Should().Equal(["good"], "the host started anyway and the well-formed skills landed");
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_duplicate_name_across_roots_keeps_the_first_configured_root_and_names_both_files()
    {
        // 'zulu' is configured first but sorts last, and its copy is written first: the winner is the first
        // *configured* root, which is neither the alphabetically first one nor the last one written.
        using var zulu = new SkillFolder("zulu");
        using var alpha = new SkillFolder("alpha");
        zulu.WriteFolderSkill("release", "From the repo.");
        alpha.WriteFolderSkill("release", "From the shared folder.");
        var (sync, store, log) = Build(Clock(), Roots(zulu.Root, alpha.Root));

        var result = await sync.SyncAsync(CancellationToken.None);

        result.Value.Should().Be(new SkillSyncReport(1, 1, 0, 1, 0));
        store.Upserts.Should().Equal(["release"], "the losing copy is never written at all");
        (await store.GetAsync(SkillName.Parse("release"), CancellationToken.None)).Value.Description.Should().Be("From the repo.");
        var duplicate = log.Entries.Should().ContainSingle(e => e.EventId == 565 && e.Level == LogLevel.Warning).Subject;
        duplicate.Message.Should().Contain(zulu.Root).And.Contain(alpha.Root, "both copies are called SKILL.md, so only the full paths tell the operator which file lost");
    }

    [Fact]
    public async Task One_unreadable_root_is_ignored_while_the_others_sync()
    {
        using var good = new SkillFolder();
        good.WriteFolderSkill("release");
        var missing = Path.Combine(Path.GetTempPath(), "thalos-skills-missing-" + Guid.NewGuid().ToString("N"));
        var (sync, store, log) = Build(Clock(), Roots(missing, good.Root));

        var result = await sync.SyncAsync(CancellationToken.None);

        result.Value.Scanned.Should().Be(1);
        log.Entries.Should().Contain(e => e.EventId == 562 && e.Level == LogLevel.Warning);
        store.DeactivateCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task When_no_root_can_be_read_nothing_is_deactivated()
    {
        var clock = Clock();
        var missing = Path.Combine(Path.GetTempPath(), "thalos-skills-missing-" + Guid.NewGuid().ToString("N"));
        var (sync, store, log) = Build(clock, Roots(missing));
        await store.UpsertAsync(SkillModelTests.Doc("planted"), CancellationToken.None);

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new SkillSyncReport(0, 0, 0, 0, 0));
        log.Entries.Should().Contain(e => e.EventId == 566 && e.Level == LogLevel.Error);
        store.DeactivateCalls.Should().BeEmpty("a path typo must never deactivate the whole library");
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task A_store_failure_is_returned_and_fails_the_host_start()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var options = Roots(folder.Root);
        var (sync, store, _) = Build(Clock(), options);
        store.OnUpsert = _ => AgentError.SkillStoreFailed("the store is down", "NpgsqlException");

        var result = await sync.SyncAsync(CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillStoreFailed);

        // an agent silently missing its procedures is worse than a host that will not start
        using var host = BuildHost(store, options, Clock());
        var start = async () => await host.StartAsync(CancellationToken.None);
        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("start-up skill sync failed");
    }

    [Fact]
    public async Task A_failing_list_or_deactivate_is_also_fatal()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var options = Roots(folder.Root);
        var (sync, store, _) = Build(Clock(), options);

        store.OnList = () => AgentError.SkillStoreFailed("no reads");
        var read = await sync.SyncAsync(CancellationToken.None);
        read.IsFailure.Should().BeTrue();
        read.Error.Message.Should().Be("no reads");

        store.OnList = null;
        store.OnDeactivate = () => AgentError.SkillStoreFailed("no writes");
        var sweep = await sync.SyncAsync(CancellationToken.None);
        sweep.IsFailure.Should().BeTrue();
        sweep.Error.Message.Should().Be("no writes");

        using var host = BuildHost(store, options, Clock());
        var start = async () => await host.StartAsync(CancellationToken.None);
        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("no writes");
    }

    [Fact]
    public async Task StartingAsync_does_nothing_when_skills_or_the_startup_sync_are_disabled()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");

        var off = Roots(folder.Root);
        off.Enabled = false;
        var (disabled, disabledStore, log) = Build(Clock(), off);
        await disabled.StartingAsync(CancellationToken.None);
        (await disabledStore.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().BeEmpty();
        log.Entries.Should().Contain(e => e.EventId == 567);

        var noSync = Roots(folder.Root);
        noSync.SyncOnStartup = false;
        var (manual, manualStore, _) = Build(Clock(), noSync);
        await manual.StartingAsync(CancellationToken.None);
        (await manualStore.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().BeEmpty();

        await manual.SyncAsync(CancellationToken.None);
        (await manualStore.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle("SyncAsync still works when only the start-up hook is off");
    }

    [Fact]
    public async Task The_host_syncs_before_any_other_hosted_service_starts()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var store = new RecordingSkillStore(new InMemorySkillStore(Clock()));
        var probe = new StoreProbe(store);
        using var host = BuildHost(store, Roots(folder.Root), Clock(), probe);

        await host.StartAsync(CancellationToken.None);

        probe.SkillsVisibleAtStart.Should().Be(1, "StartingAsync runs before every hosted service's StartAsync, so the catalogue is populated before the first turn");
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_service_is_a_hosted_lifecycle_service()
    {
        using var folder = new SkillFolder();
        var (sync, _, _) = Build(Clock(), Roots(folder.Root));
        sync.Should().BeAssignableTo<IHostedLifecycleService>("StartingAsync runs before any other hosted service starts");
    }
}
