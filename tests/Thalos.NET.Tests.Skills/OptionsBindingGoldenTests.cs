using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Skills;
using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills;

/// <summary>
/// <see cref="SkillOptions"/> as <c>UseSkills(IConfiguration)</c> binds them and <see cref="CharterOptions.Roots"/> as
/// <c>UseRoleCharters(IConfiguration)</c> reads them, captured before both move to the configuration binder source
/// generator. Each starts from a root added in code first, so the dump shows whether configuration appends to the
/// collection or replaces it. Roots are full paths on the host, so the base directory is written as <c>{base}</c>.
/// The dump is written by the reflection-based serializer in test code, which stays allowed as an oracle.
/// Characterization tests: they pass on today's code.
/// </summary>
public sealed class OptionsBindingGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string Base = Path.GetFullPath("golden-roots");

    // Red: rename SkillOptions.SectionName to "Thalos:SkillsX"; nothing binds and the dump shows the defaults.
    [Fact]
    public void Skill_options_bind_as_before()
    {
        var configuration = Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Skills:Enabled"] = "true",
            ["Thalos:Skills:Roots:0"] = Path.Combine(Base, "team"),
            ["Thalos:Skills:Roots:1"] = Path.Combine(Base, "code"),
            ["Thalos:Skills:Catalogue:MaxChars"] = "1200",
            ["Thalos:Skills:Search:TopK"] = "3",
            ["Thalos:Skills:Search:MinScore"] = "0.5",
            ["Thalos:Skills:ExposeTools"] = "false",
            ["Thalos:Skills:SyncOnStartup"] = "false",
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<SkillOptions>(o => o.Roots.Add(Path.Combine(Base, "in-code")));
        services.AddThalos(t => t.UseSkills(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = Dump(provider.GetRequiredService<IOptions<SkillOptions>>().Value);

        dump.Should().Be("""
            {
              "Enabled": true,
              "Roots": [
                "{base}/in-code",
                "{base}/team",
                "{base}/code"
              ],
              "Catalogue": {
                "MaxChars": 1200
              },
              "Search": {
                "TopK": 3,
                "MinScore": 0.5
              },
              "ExposeTools": false,
              "SyncOnStartup": false
            }
            """);
    }

    // Red: in UseRoleCharters(IConfiguration), read the section "RootsX"; no root binds and the dump shows none.
    [Fact]
    public void Charter_roots_bind_as_before()
    {
        var configuration = Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Charters:Roots:0"] = Path.Combine(Base, "charters"),
            ["Thalos:Charters:Roots:1"] = Path.Combine(Base, "more"),
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CharterOptions>(o => o.Roots.Add(Path.Combine(Base, "in-code")));
        services.AddThalos(t => t.UseRoleCharters(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = Dump(provider.GetRequiredService<IOptions<CharterOptions>>().Value);

        dump.Should().Be("""
            {
              "Roots": [
                "{base}/charters",
                "{base}/more"
              ],
              "Envelopes": []
            }
            """);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string Dump<T>(T options) =>
        JsonSerializer.Serialize(options, DumpOptions)
            .Replace(JsonSerializer.Serialize(Base, DumpOptions).Trim('"'), "{base}", StringComparison.Ordinal)
            .Replace(@"\\", "/", StringComparison.Ordinal);
}
