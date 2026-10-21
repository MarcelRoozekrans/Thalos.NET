using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Anthropic;

namespace Thalos.Tests.Unit.Anthropic;

/// <summary>
/// <see cref="AnthropicOptions"/> as <c>UseAnthropic(IConfiguration)</c> binds them, captured before the bind moves to
/// the configuration binder source generator, including the get-only <see cref="AnthropicOptions.PromptCaching"/>
/// object the binder fills in place. The dump is written by the reflection-based serializer in test code, which stays
/// allowed as an oracle. A characterization test: it passes on today's code.
/// </summary>
public sealed class OptionsBindingGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Red: rename AnthropicOptions.SectionName to "Thalos:AnthropicX"; nothing binds and the dump shows the defaults.
    [Fact]
    public void Anthropic_options_bind_as_before()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:ApiKey"] = "sk-test",
            ["Thalos:Anthropic:DefaultModel"] = "claude-test",
            ["Thalos:Anthropic:DefaultMaxOutputTokens"] = "4096",
            ["Thalos:Anthropic:Timeout"] = "00:02:30",
            ["Thalos:Anthropic:MaxRetries"] = "3",
            ["Thalos:Anthropic:PromptCaching:Enabled"] = "false",
            ["Thalos:Anthropic:PromptCaching:Ttl"] = "1h",
            ["Thalos:Anthropic:Unknown"] = "ignored",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t => t.UseAnthropic(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = JsonSerializer.Serialize(provider.GetRequiredService<IOptions<AnthropicOptions>>().Value, DumpOptions);

        dump.Should().Be("""
            {
              "ApiKey": "sk-test",
              "DefaultModel": "claude-test",
              "DefaultMaxOutputTokens": 4096,
              "Timeout": "00:02:30",
              "MaxRetries": 3,
              "PromptCaching": {
                "Enabled": false,
                "Ttl": "1h"
              }
            }
            """);
    }
}
