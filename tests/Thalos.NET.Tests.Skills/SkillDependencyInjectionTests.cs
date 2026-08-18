using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Skills;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Skills;

/// <summary>
/// The DI surface: what <c>UseSkills</c> puts in the container, what <c>UseSkillStore</c>/<c>UseSkillIndex</c> replace, how the
/// options are normalised and validated, and the two guarantees the wiring exists to make — a host with no embedding generator
/// still works, and <c>Enabled = false</c> is a real off switch.
/// </summary>
public sealed class SkillDependencyInjectionTests
{
    private const string Unavailable = "Skill search is unavailable; the <skills> block in your instructions lists every skill you can load.";

    /// <summary>A caller for <see cref="TurnScope.Begin"/>; tool invocation needs a turn.</summary>
    private sealed class TestCaller : ISecurityContext
    {
        public string Id => "alice";

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static AgentDefinition Agent() => new() { Id = AgentId.New(), Name = "a", Instructions = "i", Skills = ["*"] };

    private static IChatClientProvider Provider()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        return provider;
    }

    private static ServiceProvider Build(Action<ThalosBuilder>? extra = null, bool withEmbeddings = true, Action<SkillOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (withEmbeddings)
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(512));
        }

        services.AddThalos(t =>
        {
            t.UseChatClientProvider(Provider()).UseInMemorySessionStore().UseSkills(configure ?? (o => o.Roots.Add(Path.GetTempPath())));
            extra?.Invoke(t);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>A real host wired the way production wires it, so hosted services, option validation and lifecycle order are all live.</summary>
    private static IHost BuildHost(AgentDefinition agent, string? root, bool withEmbeddings = false, Action<SkillOptions>? configure = null, Action<IServiceCollection>? before = null) =>
        new HostBuilder().ConfigureServices(services =>
        {
            before?.Invoke(services);
            if (withEmbeddings)
            {
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(512));
            }

            services.AddThalos(t => t.UseChatClientProvider(Provider()).UseInMemorySessionStore().AddAgent(agent).UseSkills(o =>
            {
                if (root is not null)
                {
                    o.Roots.Add(root);
                }

                configure?.Invoke(o);
            }));
        }).Build();

    private static async Task<List<AIFunction>> ToolsAsync(IHost host, AgentDefinition agent)
    {
        var resolved = await host.Services.GetRequiredService<IToolCatalog>().ResolveAsync(agent, CancellationToken.None);
        resolved.IsSuccess.Should().BeTrue();
        return resolved.Value.Cast<AIFunction>().ToList();
    }

    [Fact]
    public void Resolves_the_store_index_catalogue_tool_source_context_source_and_sync_service()
    {
        using var sp = Build();

        sp.GetRequiredService<ISkillStore>().Should().BeOfType<SkillStoreInstrumented>();
        sp.GetRequiredService<ISkillIndex>().Should().BeOfType<InMemorySkillIndex>();
        sp.GetRequiredService<SkillCatalogue>().Should().NotBeNull();
        sp.GetServices<IToolSource>().Should().ContainSingle(s => s.Name == "skills");
        sp.GetServices<IAgentContextProviderSource>().Should().ContainSingle().Which.Should().BeOfType<SkillContextProviderSource>();
        sp.GetServices<IHostedService>().OfType<SkillSyncService>().Should().ContainSingle();
    }

    [Fact]
    public void Without_an_embedding_generator_the_index_is_the_unavailable_singleton_and_the_catalogue_still_resolves()
    {
        using var sp = Build(withEmbeddings: false);

        sp.GetRequiredService<ISkillIndex>().Should().BeSameAs(UnavailableSkillIndex.Instance);
        sp.GetRequiredService<SkillCatalogue>().Should().NotBeNull();
    }

    /// <summary>The headline resilience guarantee, through a real host: no embeddings, yet the sync runs, the catalogue is served and search answers honestly.</summary>
    [Fact]
    public async Task A_host_without_an_embedding_generator_starts_syncs_serves_a_catalogue_and_reports_search_unavailable()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut and publish a release.");
        var agent = Agent();
        using var host = BuildHost(agent, folder.Root);

        await host.StartAsync(CancellationToken.None);

        host.Services.GetRequiredService<ISkillIndex>().Should().BeSameAs(UnavailableSkillIndex.Instance);
        (await host.Services.GetRequiredService<ISkillStore>().ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
        host.Services.GetRequiredService<SkillCatalogue>().Render(["*"]).Should().Contain("release: How we cut and publish a release.");

        var search = (await ToolsAsync(host, agent)).Single(t => string.Equals(t.Name, "skills__search", StringComparison.Ordinal));
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller(), agent.Id);
        var answer = await search.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["query"] = "release" });

        answer?.ToString().Should().Be(Unavailable);
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The <c>[Singleton(As = typeof(SkillCatalogue))]</c> registration plus a manual one would be two instances: the sync would
    /// publish into one and every turn read the other, and the catalogue would be silently empty.
    /// </summary>
    [Fact]
    public async Task Exactly_one_SkillCatalogue_is_registered_and_the_sync_publishes_into_the_resolved_one()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseChatClientProvider(Provider()).UseInMemorySessionStore().UseSkills());
        services.Count(d => d.ServiceType == typeof(SkillCatalogue)).Should().Be(1, "a second descriptor is a second instance for anything resolving it by another service type");

        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var agent = Agent();
        using var host = BuildHost(agent, folder.Root);

        await host.StartAsync(CancellationToken.None);

        var catalogue = host.Services.GetRequiredService<SkillCatalogue>();
        catalogue.Should().BeSameAs(host.Services.GetRequiredService<SkillCatalogue>());
        catalogue.Render(["*"]).Should().NotBeNull("the sync published into the instance the container hands out");
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Custom_store_and_index_replace_the_defaults_in_any_order()
    {
        using var before = Build(t => t.UseSkillStore<FakeStore>().UseSkillIndex<FakeIndex>());
        await AssertFakeStoreIsWrapped(before);
        before.GetRequiredService<ISkillIndex>().Should().BeOfType<FakeIndex>();

        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseChatClientProvider(Provider()).UseInMemorySessionStore().UseSkillIndex<FakeIndex>().UseSkillStore<FakeStore>().UseSkills());
        using var after = services.BuildServiceProvider();
        await AssertFakeStoreIsWrapped(after);
        after.GetRequiredService<ISkillIndex>().Should().BeOfType<FakeIndex>();
    }

    /// <summary>The telemetry proxy must wrap the custom store, not the default one: a skill written through ISkillStore lands in FakeStore.</summary>
    private static async Task AssertFakeStoreIsWrapped(ServiceProvider sp)
    {
        var store = sp.GetRequiredService<ISkillStore>();
        store.Should().BeOfType<SkillStoreInstrumented>();
        (await store.UpsertAsync(SkillModelTests.Doc("release"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        sp.GetRequiredService<FakeStore>().Upserts.Should().ContainSingle("the proxy delegates to FakeStore");
    }

    [Fact]
    public void UseSkills_is_idempotent_and_the_last_configure_wins()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t
            .UseChatClientProvider(Provider())
            .UseInMemorySessionStore()
            .UseSkills(o => o.Catalogue.MaxChars = 111)
            .UseSkills(o => o.Catalogue.MaxChars = 222));
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOptions<SkillOptions>>().Value.Catalogue.MaxChars.Should().Be(222);
        sp.GetServices<IToolSource>().Should().ContainSingle(s => s.Name == "skills");
        sp.GetServices<IAgentContextProviderSource>().Should().ContainSingle();
        sp.GetServices<IHostedService>().OfType<SkillSyncService>().Should().ContainSingle();
        sp.GetServices<SkillCatalogue>().Should().ContainSingle();
    }

    [Fact]
    public void Options_bind_from_configuration_including_the_roots_array()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Skills:Enabled"] = "true",
            ["Thalos:Skills:Roots:0"] = "./skills",
            ["Thalos:Skills:Roots:1"] = "../shared-skills",
            ["Thalos:Skills:Catalogue:MaxChars"] = "1234",
            ["Thalos:Skills:Search:TopK"] = "7",
            ["Thalos:Skills:Search:MinScore"] = "0.42",
            ["Thalos:Skills:ExposeTools"] = "false",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseChatClientProvider(Provider()).UseInMemorySessionStore().UseSkills(configuration));
        using var sp = services.BuildServiceProvider();

        var o = sp.GetRequiredService<IOptions<SkillOptions>>().Value;
        o.Roots.Should().HaveCount(2);
        o.Roots[0].Should().EndWith("skills");
        Path.IsPathFullyQualified(o.Roots[0]).Should().BeTrue("the sync must not depend on the working directory at scan time");
        o.Catalogue.MaxChars.Should().Be(1234);
        o.Search.TopK.Should().Be(7);
        o.Search.MinScore.Should().Be(0.42);
        o.ExposeTools.Should().BeFalse();
    }

    [Fact]
    public void Blank_and_duplicate_roots_are_normalised_away()
    {
        using var sp = Build(configure: o =>
        {
            o.Roots.Add("  ");
            o.Roots.Add("./skills");
            o.Roots.Add("./skills");
            o.Roots.Add("./skills/");
        });

        sp.GetRequiredService<IOptions<SkillOptions>>().Value.Roots.Should().ContainSingle();
    }

    [Theory]
    [InlineData(-1, 5, 0.5, "Catalogue.MaxChars")]
    [InlineData(2000, 0, 0.5, "Search.TopK")]
    [InlineData(2000, -3, 0.5, "Search.TopK")]
    [InlineData(2000, 5, 1.5, "Search.MinScore")]
    [InlineData(2000, 5, -0.1, "Search.MinScore")]
    [InlineData(2000, 5, double.NaN, "Search.MinScore")]
    public void Misconfiguration_throws_at_first_resolve_naming_the_member(int maxChars, int topK, double minScore, string member)
    {
        using var sp = Build(configure: o =>
        {
            o.Catalogue.MaxChars = maxChars;
            o.Search.TopK = topK;
            o.Search.MinScore = minScore;
        });

        var act = () => sp.GetRequiredService<IOptions<SkillOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*" + member + "*");
    }

    /// <summary>
    /// Registering a validator without <c>ValidateOnStart</c> only trips on first resolve — which the sync would do anyway, so
    /// "the host threw" proves nothing on its own. The probe starts <em>before</em> the sync: validation must run first, so
    /// nothing in the host has begun starting when the misconfiguration is reported.
    /// </summary>
    [Fact]
    public async Task ValidateOnStart_fires_before_any_hosted_service_so_a_misconfigured_host_never_starts()
    {
        var probe = new StartingProbe();
        using var host = BuildHost(Agent(), root: null, configure: o => o.Search.MinScore = 4, before: services => services.AddSingleton<IHostedService>(probe));

        var start = async () => await host.StartAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*Search.MinScore*");
        probe.Starting.Should().BeFalse("ValidateOnStart runs before the first hosted service, not on the sync's first read of the options");
    }

    /// <summary>A root that does not exist is a typo, not a configuration error: the sync logs it and the library survives.</summary>
    [Fact]
    public async Task A_root_that_does_not_exist_is_not_a_validation_error_and_the_host_still_starts()
    {
        var missing = Path.Combine(Path.GetTempPath(), "thalos-skills-missing-" + Guid.NewGuid().ToString("N")[..8]);
        var agent = Agent();
        using var host = BuildHost(agent, missing);

        await host.StartAsync(CancellationToken.None);

        host.Services.GetRequiredService<IOptions<SkillOptions>>().Value.Roots.Should().ContainSingle();
        (await host.Services.GetRequiredService<ISkillStore>().ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().BeEmpty();
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>Off means off: no sync, no catalogue, no tools and no context provider — but the host still starts.</summary>
    [Fact]
    public async Task Disabled_skills_are_a_genuine_off_switch_and_the_host_still_starts()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        var agent = Agent();
        using var host = BuildHost(agent, folder.Root, configure: o => o.Enabled = false);

        await host.StartAsync(CancellationToken.None);

        (await host.Services.GetRequiredService<ISkillStore>().ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().BeEmpty("nothing synced");
        host.Services.GetRequiredService<SkillCatalogue>().Render(["*"]).Should().BeNull("nothing was published");
        (await ToolsAsync(host, agent)).Should().BeEmpty("no skills tools are offered");
        host.Services.GetServices<IAgentContextProviderSource>().Single().CreateProvider(agent).Should().BeNull("no <skills> block is injected");
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>Records whether the host got as far as starting a hosted service; registered before skills, so it is the earliest observer there is.</summary>
    private sealed class StartingProbe : IHostedLifecycleService
    {
        public bool Starting { get; private set; }

        public Task StartingAsync(CancellationToken cancellationToken)
        {
            Starting = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStore : ISkillStore
    {
        public List<SkillName> Upserts { get; } = [];

        public ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(skill);
            Upserts.Add(skill.Name);
            return new(Result<SkillDocument, AgentError>.Success(skill));
        }

        public ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct) => new(Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound(name.Value)));

        public ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct) => new(Result<IReadOnlyList<SkillDocument>, AgentError>.Success([]));

        public ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }

    private sealed class FakeIndex : ISkillIndex
    {
        public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<SkillDocument> skills, CancellationToken ct) => new(UnitResult<AgentError>.Success());

        public ValueTask<Result<IReadOnlyList<SkillHit>, AgentError>> SearchAsync(string query, SkillSearchOptions options, CancellationToken ct) => new(Result<IReadOnlyList<SkillHit>, AgentError>.Success([]));

        public ValueTask<UnitResult<AgentError>> RemoveAsync(SkillName name, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }
}
