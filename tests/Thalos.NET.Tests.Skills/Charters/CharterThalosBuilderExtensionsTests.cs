using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class CharterThalosBuilderExtensionsTests
{
    /// <summary>
    /// Builds a real <see cref="IHost"/> and starts it — not just a bare <see cref="ServiceProvider"/> read via
    /// <c>IOptions.Value</c> — because <c>Host.StartAsync</c> is what actually runs every registered
    /// <c>IStartupValidator</c> (the mechanism behind <c>ValidateOnStart</c>) before any hosted service, including
    /// <see cref="CharterSyncService"/>, gets to run. Reading <c>IOptions&lt;CharterOptions&gt;.Value</c> directly would
    /// also throw, since the validator runs on every options resolution, but it would prove nothing about
    /// <c>ValidateOnStart</c> specifically — removing that call would not turn such a test red.
    /// </summary>
    private static async Task<OptionsValidationException> ExpectValidationFailureAtStartAsync(Action<ThalosBuilder> configure)
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddThalos(configure)).Build();
        var act = async () => await host.StartAsync();
        return (await act.Should().ThrowAsync<OptionsValidationException>()).Which;
    }

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
        // Envelopes:0:Name is present on purpose: a whole-object Bind() *would* populate this (ConfigurationBinder
        // fills get-only collection properties), producing an envelope with no Id since AgentId cannot be expressed
        // in configuration. This key proves the overload never reaches for it, not merely that the section is empty.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("Thalos:Charters:Roots:0", "roles"),
                new KeyValuePair<string, string?>("Thalos:Charters:Envelopes:0:Name", "reviewer"),
            ])
            .Build();
        var services = new ServiceCollection();
        services.AddThalos(builder => builder.UseRoleCharters(configuration));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CharterOptions>>().Value;

        options.Roots.Should().Equal("roles");
        options.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_envelope_name_that_collides_with_a_config_agent_name_fails_validation_at_host_start()
    {
        var error = await ExpectValidationFailureAtStartAsync(builder =>
        {
            builder.AddAgent(new AgentDefinition { Id = AgentId.New(), Name = "reviewer", Instructions = "i" });
            builder.UseRoleCharters(o => o.Envelopes.Add(new AgentEnvelope { Id = AgentId.New(), Name = "reviewer", Tools = ["*"] }));
        });

        error.Message.Should().Contain("reviewer");
    }

    [Fact]
    public async Task An_envelope_id_that_collides_with_a_config_agent_id_fails_validation_at_host_start()
    {
        var sharedId = AgentId.New();
        var error = await ExpectValidationFailureAtStartAsync(builder =>
        {
            builder.AddAgent(new AgentDefinition { Id = sharedId, Name = "architect", Instructions = "i" });
            builder.UseRoleCharters(o => o.Envelopes.Add(new AgentEnvelope { Id = sharedId, Name = "reviewer", Tools = ["*"] }));
        });

        error.Message.Should().Contain(sharedId.ToString());
    }

    [Fact]
    public async Task Two_envelopes_with_the_same_name_fail_validation_at_host_start()
    {
        var error = await ExpectValidationFailureAtStartAsync(builder => builder.UseRoleCharters(o =>
        {
            o.Envelopes.Add(new AgentEnvelope { Id = AgentId.New(), Name = "reviewer", Tools = ["*"] });
            o.Envelopes.Add(new AgentEnvelope { Id = AgentId.New(), Name = "reviewer", Tools = ["*"] });
        }));

        error.Message.Should().Contain("reviewer");
    }

    [Fact]
    public async Task Two_envelopes_with_the_same_id_fail_validation_at_host_start()
    {
        var sharedId = AgentId.New();
        var error = await ExpectValidationFailureAtStartAsync(builder => builder.UseRoleCharters(o =>
        {
            o.Envelopes.Add(new AgentEnvelope { Id = sharedId, Name = "reviewer", Tools = ["*"] });
            o.Envelopes.Add(new AgentEnvelope { Id = sharedId, Name = "implementer", Tools = ["*"] });
        }));

        error.Message.Should().Contain(sharedId.ToString());
    }
}
