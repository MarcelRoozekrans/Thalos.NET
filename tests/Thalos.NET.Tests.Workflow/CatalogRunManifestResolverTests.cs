using Thalos;
using Thalos.Skills;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="CatalogRunManifestResolver"/>: every task node pinned to the agent revision and active
/// skill hash the host's own <see cref="IWorkflowReferenceResolver"/> and catalogues actually resolved, never to
/// the raw name written in the process file.
/// </summary>
public sealed class CatalogRunManifestResolverTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    /// <summary>
    /// Two task nodes ("implement", "review") feeding an approval gate feeding a terminal — the gate and terminal
    /// exist specifically so <see cref="Every_task_node_is_pinned_to_the_resolved_agent_revision_and_active_skill_hash"/>
    /// has something present in the graph that must <em>not</em> appear in the resolved manifest.
    /// </summary>
    private const string TwoNodeProcessYaml = """
        process: manufacture
        version: 1
        nodes:
          implement:
            agent: implementer
            skill: manufacture-implement
            next: review
          review:
            agent: reviewer
            skill: manufacture-review
            next: gate
          gate: { await: human_approval, next: done }
          done: { terminal: succeeded }
        """;

    [Fact]
    public async Task Every_task_node_is_pinned_to_the_resolved_agent_revision_and_active_skill_hash()
    {
        var resolver = await Build(
            agents: [Def("implementer", revision: "r-imp"), Def("reviewer", revision: "r-rev")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);

        var manifest = (await resolver.ResolveAsync(TwoNodeProcess(), null, CancellationToken.None)).Value;

        manifest.Nodes.Keys.Should().BeEquivalentTo(["implement", "review"], "gates and terminals are not pinned");
        manifest.Nodes["review"].Should().BeEquivalentTo(new { AgentName = "reviewer", AgentRevision = "r-rev", SkillName = "manufacture-review", SkillHash = "h-rev" });
        manifest.Nodes["implement"].Should().BeEquivalentTo(new { AgentName = "implementer", AgentRevision = "r-imp", SkillName = "manufacture-implement", SkillHash = "h-imp" });
    }

    [Fact]
    public async Task The_reference_resolver_decides_the_agent_so_a_host_mapping_is_pinned_visibly()
    {
        // FakeWorkflowReferenceResolver maps every name to the "fallback" agent's id — standing in for a host
        // that maps process-file names (squad roles, say) onto its own concrete agents.
        var resolver = await Build(
            agents: [Def("fallback")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")],
            mapAllTo: "fallback");

        var manifest = (await resolver.ResolveAsync(TwoNodeProcess(), null, CancellationToken.None)).Value;

        manifest.Nodes.Values.Should().OnlyContain(p => string.Equals(p.AgentName, "fallback", StringComparison.Ordinal), "the pin must record what the resolver actually decided, not the process file's own agent name");
    }

    [Fact]
    public async Task An_inactive_skill_is_not_pinned_even_though_the_store_still_returns_its_row()
    {
        var resolver = await Build(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev", active: false)]);

        var result = await resolver.ResolveAsync(TwoNodeProcess(), null, CancellationToken.None);

        result.IsFailure.Should().BeTrue("ISkillStore.GetAsync still returns an inactive row — the resolver, not the store, must refuse to pin it");
        result.Error.Should().Contain("review").And.Contain("manufacture-review");
    }

    [Fact]
    public async Task An_unresolvable_agent_fails_naming_the_node()
    {
        var resolver = await Build(
            agents: [Def("implementer")], // "reviewer" is missing
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);

        var result = await resolver.ResolveAsync(TwoNodeProcess(), null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("review");
    }

    [Fact]
    public async Task Documents_are_carried_into_the_manifest_unchanged()
    {
        var resolver = await Build(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["standing_instructions"] = "Run dotnet test." };

        var manifest = (await resolver.ResolveAsync(TwoNodeProcess(), documents, CancellationToken.None)).Value;

        manifest.Documents["standing_instructions"].Should().Be("Run dotnet test.");
    }

    private static ProcessDefinition TwoNodeProcess() => ProcessLoader.Load(TwoNodeProcessYaml).Value;

    /// <summary>
    /// Builds a <see cref="CatalogRunManifestResolver"/> over a <see cref="FakeAgentCatalog"/> and an
    /// <see cref="InMemorySkillStore"/> seeded with <paramref name="agents"/> and <paramref name="skills"/>. The
    /// reference resolver maps each agent's own name to its id, unless <paramref name="mapAllTo"/> names a single
    /// agent every node name should resolve to instead — the shape a host's own squad-role mapping takes.
    /// </summary>
    private static async Task<CatalogRunManifestResolver> Build(IReadOnlyList<AgentDefinition> agents, IReadOnlyList<SkillDocument> skills, string? mapAllTo = null)
    {
        var byName = new Dictionary<string, AgentId>(StringComparer.Ordinal);
        if (mapAllTo is not null)
        {
            var fallbackId = agents.Single(a => string.Equals(a.Name, mapAllTo, StringComparison.Ordinal)).Id;
            foreach (var name in new[] { "implementer", "reviewer" })
            {
                byName[name] = fallbackId;
            }
        }
        else
        {
            foreach (var agent in agents)
            {
                byName[agent.Name] = agent.Id;
            }
        }

        var references = new FakeWorkflowReferenceResolver(byName);
        var catalog = new FakeAgentCatalog(agents);
        var skillStore = new InMemorySkillStore(Clock);
        foreach (var skill in skills)
        {
            // ISkillStore.UpsertAsync stores the document "as given" (see its own contract doc), so a skill built
            // with IsActive = false is seeded inactive directly — no separate deactivate step needed.
            await skillStore.UpsertAsync(skill, CancellationToken.None);
        }

        return new CatalogRunManifestResolver(references, catalog, skillStore);
    }

    private static AgentDefinition Def(string name, string? revision = null) =>
        new() { Id = AgentId.New(), Name = name, Instructions = "do the thing", Revision = revision };

    private static SkillDocument Skill(string name, string hash, bool active = true) => new()
    {
        Name = SkillName.Parse(name),
        Description = "a test skill",
        Body = "do the thing",
        SourcePath = $"{name}/SKILL.md",
        ContentHash = hash,
        IsActive = active,
        UpdatedAt = Clock.GetUtcNow(),
    };
}
