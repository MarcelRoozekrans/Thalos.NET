using Microsoft.Extensions.Options;
using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class CharteredAgentCatalogTests
{
    private static readonly AgentId ReviewerId = AgentId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static AgentEnvelope Envelope(string role, IReadOnlyList<string>? tools = null) => new()
    {
        Id = ReviewerId,
        Name = role,
        Tools = tools ?? ["*"],
    };

    private static RoleCharter Charter(string role, string hash, string instructions = "x", string? model = null, bool active = true) => new()
    {
        Role = role,
        Description = "A role.",
        Instructions = instructions,
        Model = model,
        SourcePath = $"{role}.md",
        ContentHash = hash,
        IsActive = active,
        UpdatedAt = Now,
    };

    private static AgentDefinition Get(CharteredAgentCatalog catalog, AgentId id, string? revision = null)
    {
        catalog.TryGet(id, revision, out var definition).Should().BeTrue();
        return definition!;
    }

    private static CharteredAgentCatalog CatalogWith(params AgentEnvelope[] envelopes) => CatalogWith([], envelopes);

    private static CharteredAgentCatalog CatalogWith(IReadOnlyList<AgentDefinition> configAgents, params AgentEnvelope[] envelopes)
    {
        var thalos = new ThalosOptions();
        foreach (var agent in configAgents)
        {
            thalos.Agents.Add(agent);
        }

        var charters = new CharterOptions();
        foreach (var envelope in envelopes)
        {
            charters.Envelopes.Add(envelope);
        }

        return new CharteredAgentCatalog(Options.Create(thalos), Options.Create(charters));
    }

    [Fact]
    public void The_composed_definition_takes_its_tools_from_the_envelope_and_its_prose_from_the_charter()
    {
        var catalog = CatalogWith(Envelope("reviewer", tools: ["roslyn__find_*"]));
        catalog.Set([Charter("reviewer", "h1", instructions: "Review it.", model: "claude-opus-5")]);

        var d = Get(catalog, ReviewerId);
        d.Tools.Should().Equal("roslyn__find_*");
        d.Instructions.Should().Be("Review it.");
        d.Model.Should().Be("claude-opus-5");
        d.Revision.Should().Be("h1");
    }

    [Fact]
    public void A_pinned_revision_is_served_after_the_charter_moves_on()
    {
        var catalog = CatalogWith(Envelope("reviewer"));
        catalog.Set([Charter("reviewer", "h1", "Old.", active: false), Charter("reviewer", "h2", "New.", active: true)]);

        var pinned = Get(catalog, ReviewerId, "h1");
        pinned.Instructions.Should().Be("Old.");
        var current = Get(catalog, ReviewerId);
        current.Instructions.Should().Be("New.");
        catalog.TryGet(ReviewerId, "h-unknown", out _).Should().BeFalse("an unknown pin fails; it never falls back to current");
    }

    [Fact]
    public void Config_agents_pass_through_unchanged()
    {
        var architect = TestAgents.Definition() with { Name = "Daedalus Architect" };
        var catalog = CatalogWith(configAgents: [architect], Envelope("reviewer"));
        catalog.Set([Charter("reviewer", "h1", "x")]);

        catalog.Agents.Should().Contain(architect);
        catalog.Agents.Select(a => a.Name).Should().BeEquivalentTo(["Daedalus Architect", "reviewer"]);
    }

    [Fact]
    public void An_envelope_with_no_active_charter_composes_into_no_agent()
    {
        var catalog = CatalogWith(Envelope("reviewer"));

        catalog.Set([]);

        catalog.TryGet(ReviewerId, out _).Should().BeFalse();
        catalog.Agents.Should().BeEmpty();
    }

    [Fact]
    public void A_charter_whose_role_matches_no_envelope_is_not_served()
    {
        var catalog = CatalogWith(Envelope("reviewer"));

        catalog.Set([Charter("implementer", "h1", "x")]);

        catalog.Agents.Should().BeEmpty();
    }
}
