using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Anthropic;

namespace Thalos.Tests.Unit.Anthropic;

public sealed class AnthropicProviderTests
{
    private static AgentDefinition Agent(string? model = null) => new() { Id = AgentId.New(), Name = "a", Instructions = "i", Model = model };

    [Fact]
    public void UseAnthropic_registers_provider_with_defaults()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseAnthropic(o => { o.ApiKey = "sk-test"; o.DefaultModel = "claude-sonnet-5"; }).UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();

        var provider = sp.GetRequiredService<IChatClientProvider>();
        provider.Should().BeOfType<AnthropicChatClientProvider>();
        provider.Name.Should().Be("anthropic");
        provider.DefaultModel.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void CreateChatClient_returns_a_client_and_honours_agent_model()
    {
        using var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "sk-test", DefaultModel = "d", DefaultMaxOutputTokens = 1024 }));
        var client = provider.CreateChatClient(Agent("claude-opus-5"));
        client.Should().NotBeNull();
        var meta = client.GetService<ChatClientMetadata>();
        meta!.DefaultModelId.Should().Be("claude-opus-5");
    }

    [Fact]
    public void Missing_api_key_throws_on_first_use()
    {
        using var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "" }), _ => null);
        var act = () => provider.CreateChatClient(Agent());
        act.Should().Throw<InvalidOperationException>().WithMessage("*ANTHROPIC_API_KEY*");
        provider.IsClientCreated.Should().BeFalse();
    }

    [Fact]
    public void Falls_back_to_environment_variable_when_ApiKey_is_empty()
    {
        string? requested = null;
        using var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = " " }), name => { requested = name; return "sk-env"; });
        var act = () => provider.CreateChatClient(Agent());
        act.Should().NotThrow();
        requested.Should().Be("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void Shares_one_transport_across_agents_and_disposes_it()
    {
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "sk-test" }));
        provider.IsClientCreated.Should().BeFalse("the SDK client is created lazily");

        using var first = provider.CreateChatClient(Agent("m1"));
        using var second = provider.CreateChatClient(Agent("m2"));

        provider.IsClientCreated.Should().BeTrue();
        first.Should().NotBeSameAs(second);
        first.GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("m1");
        second.GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("m2");

        var dispose = () => provider.Dispose();
        dispose.Should().NotThrow();
    }
}
