using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Anthropic;

namespace Thalos.Tests.Unit.Anthropic;

public sealed class AnthropicProviderTests
{
    [Fact]
    public void UseAnthropic_registers_provider_with_defaults()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseAnthropic(o => { o.ApiKey = "sk-test"; o.DefaultModel = "claude-sonnet-4-5"; }).UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();

        var provider = sp.GetRequiredService<IChatClientProvider>();
        provider.Should().BeOfType<AnthropicChatClientProvider>();
        provider.Name.Should().Be("anthropic");
        provider.DefaultModel.Should().Be("claude-sonnet-4-5");
    }

    [Fact]
    public void CreateChatClient_returns_a_client_and_honours_agent_model()
    {
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "sk-test", DefaultModel = "d", DefaultMaxOutputTokens = 1024 }));
        var client = provider.CreateChatClient(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i", Model = "claude-opus-4-1" });
        client.Should().NotBeNull();
        var meta = client.GetService<ChatClientMetadata>();
        meta!.DefaultModelId.Should().Be("claude-opus-4-1");
    }

    [Fact]
    public void Missing_api_key_throws_on_first_use()
    {
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "" }));
        var act = () => provider.CreateChatClient(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" });
        act.Should().Throw<InvalidOperationException>().WithMessage("*ANTHROPIC_API_KEY*");
    }
}
