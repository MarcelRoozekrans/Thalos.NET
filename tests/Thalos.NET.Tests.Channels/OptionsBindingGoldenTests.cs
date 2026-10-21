using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Channels;

namespace Thalos.Tests.Channels;

/// <summary>
/// <see cref="ChannelOptions"/> as <c>UseChannels(IConfiguration)</c> binds them, captured before the bind moves to the
/// configuration binder source generator. The dump is written by the reflection-based serializer in test code, which
/// stays allowed as an oracle. A characterization test: it passes on today's code.
/// </summary>
public sealed class OptionsBindingGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Red: rename ChannelOptions.SectionName to "Thalos:ChannelsX"; nothing binds and the dump shows the defaults.
    [Fact]
    public void Channel_options_bind_as_before()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Channels:Enabled"] = "false",
            ["Thalos:Channels:DefaultAgent"] = "assistant",
            ["Thalos:Channels:IdleTimeout"] = "02:30:00",
            ["Thalos:Channels:FlushInterval"] = "00:00:00.250",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t => t.UseChannels(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = JsonSerializer.Serialize(provider.GetRequiredService<IOptions<ChannelOptions>>().Value, DumpOptions);

        dump.Should().Be("""
            {
              "Enabled": false,
              "DefaultAgent": "assistant",
              "IdleTimeout": "02:30:00",
              "FlushInterval": "00:00:00.2500000"
            }
            """);
    }
}
