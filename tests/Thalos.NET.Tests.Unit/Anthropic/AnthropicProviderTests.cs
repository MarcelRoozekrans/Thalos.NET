using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Anthropic;
using ZeroAlloc.Rest.Anthropic;

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

    [Fact]
    public void A_provider_without_a_timeout_uses_ten_minutes()
    {
        using var http = new HttpClient(new OverloadedHandler());
        using var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicOptions { ApiKey = "sk-test" }), http);

        using var client = provider.CreateChatClient(Agent());

        // Red: in CreateClient, set Timeout = _options.Timeout ?? TimeSpan.FromSeconds(100); the HttpClient default then stays.
        http.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void A_provider_timeout_is_the_http_timeout()
    {
        using var http = new HttpClient(new OverloadedHandler());
        using var provider = new AnthropicChatClientProvider(
            Options.Create(new AnthropicOptions { ApiKey = "sk-test", Timeout = TimeSpan.FromSeconds(42) }), http);

        using var client = provider.CreateChatClient(Agent());

        // Red: in CreateClient, leave AnthropicClientOptions.Timeout at its default; the timeout is then ten minutes.
        http.Timeout.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task MaxRetries_sets_the_attempts(int maxRetries, int attempts)
    {
        var handler = new OverloadedHandler();
        using var http = new HttpClient(handler);
        using var provider = new AnthropicChatClientProvider(
            Options.Create(new AnthropicOptions { ApiKey = "sk-test", MaxRetries = maxRetries }), http);
        using var client = provider.CreateChatClient(Agent());

        var act = () => client.GetResponseAsync([new(ChatRole.User, "q")], cancellationToken: CancellationToken.None);

        // Red: in AnthropicChatClient.GetResponseAsync, return an empty ChatResponse on failure instead of throwing.
        await act.Should().ThrowAsync<AnthropicApiException>();
        // Red: in CreateClient, leave AnthropicClientOptions.MaxRetries at its default of 2; both rows then make three
        // requests. For the second row alone: always set MaxRetries = 0.
        handler.Requests.Should().Be(attempts);
    }

    /// <summary>Answers every request with 529 overloaded_error and counts the requests.</summary>
    private sealed class OverloadedHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)529)
            {
                Content = new StringContent(
                    "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
