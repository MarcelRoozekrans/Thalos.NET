using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class CharterThalosBuilderExtensionsTests
{
    [Fact]
    public void The_Action_overload_registers_envelopes_that_the_catalog_composes()
    {
        var reviewerId = AgentId.New();
        var services = new ServiceCollection();
        services.AddThalos(builder => builder.UseRoleCharters(o =>
            o.Envelopes.Add(new AgentEnvelope { Id = reviewerId, Name = "reviewer", Tools = ["roslyn__find_*"] })));

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        catalog.Should().BeOfType<CharteredAgentCatalog>();

        ((CharteredAgentCatalog)catalog).Set([new RoleCharter
        {
            Role = "reviewer",
            Description = "A role.",
            Instructions = "Review it.",
            SourcePath = "reviewer.md",
            ContentHash = "h1",
            UpdatedAt = DateTimeOffset.UtcNow,
        }]);

        catalog.TryGet(reviewerId, out var definition).Should().BeTrue();
        definition!.Tools.Should().Equal("roslyn__find_*");
        definition.Instructions.Should().Be("Review it.");
    }

    [Fact]
    public void UseRoleCharters_replaces_the_default_agent_catalog_registered_later_by_AddThalos()
    {
        var services = new ServiceCollection();
        services.AddThalos(builder => builder.UseRoleCharters());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAgentCatalog>().Should().BeOfType<CharteredAgentCatalog>(
            "AddThalos's later TryAddSingleton<IAgentCatalog, OptionsAgentCatalog> must be a no-op once UseRoleCharters has registered one");
    }

    [Fact]
    public void The_IConfiguration_overload_binds_only_roots_and_never_touches_envelopes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Thalos:Charters:Roots:0", "roles")])
            .Build();
        var services = new ServiceCollection();
        services.AddThalos(builder => builder.UseRoleCharters(configuration));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CharterOptions>>().Value;

        options.Roots.Should().Equal("roles");
        options.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public void An_envelope_name_that_collides_with_a_config_agent_fails_validation_at_start()
    {
        var services = new ServiceCollection();
        services.AddThalos(builder =>
        {
            builder.AddAgent(new AgentDefinition { Id = AgentId.New(), Name = "reviewer", Instructions = "i" });
            builder.UseRoleCharters(o => o.Envelopes.Add(new AgentEnvelope { Id = AgentId.New(), Name = "reviewer", Tools = ["*"] }));
        });

        using var provider = services.BuildServiceProvider();
        var act = () => _ = provider.GetRequiredService<IOptions<CharterOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("reviewer");
    }
}
