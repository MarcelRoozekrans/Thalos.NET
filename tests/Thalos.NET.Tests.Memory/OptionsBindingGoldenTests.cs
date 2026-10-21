using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Memory;

namespace Thalos.Tests.Memory;

/// <summary>
/// <see cref="MemoryOptions"/> as <c>UseMemory(IConfiguration)</c> binds them, nested objects included, captured before
/// the bind moves to the configuration binder source generator. The dump is written by the reflection-based serializer
/// in test code, which stays allowed as an oracle. A characterization test: it passes on today's code.
/// </summary>
public sealed class OptionsBindingGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Red: rename MemoryOptions.SectionName to "Thalos:MemoryX"; nothing binds and the dump shows the defaults.
    [Fact]
    public void Memory_options_bind_as_before()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Memory:Enabled"] = "true",
            ["Thalos:Memory:SharedOwnerId"] = "daedalus",
            ["Thalos:Memory:ExposeTools"] = "false",
            ["Thalos:Memory:Recall:TopK"] = "8",
            ["Thalos:Memory:Recall:MinScore"] = "0.45",
            ["Thalos:Memory:Recall:MaxChars"] = "1500",
            ["Thalos:Memory:Dedupe:Enabled"] = "false",
            ["Thalos:Memory:Dedupe:Threshold"] = "0.9",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t => t.UseMemory(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = JsonSerializer.Serialize(provider.GetRequiredService<IOptions<MemoryOptions>>().Value, DumpOptions);

        dump.Should().Be("""
            {
              "Enabled": true,
              "SharedOwnerId": "daedalus",
              "Recall": {
                "TopK": 8,
                "MinScore": 0.45,
                "MaxChars": 1500
              },
              "Dedupe": {
                "Enabled": false,
                "Threshold": 0.9
              },
              "ExposeTools": false
            }
            """);
    }
}
