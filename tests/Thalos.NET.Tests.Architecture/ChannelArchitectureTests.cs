using System.Xml.Linq;
using Assembly = System.Reflection.Assembly;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Thalos.Tests.Architecture;

/// <summary>
/// Pins the structural decisions specific to the channel package split: <c>Thalos.NET.Channels</c> is the
/// consumer-facing abstraction and must stay usable without pulling in a transport; <c>Thalos.NET.Channels.Telegram</c>
/// is one transport built on top of it. The dependency runs one way only.
/// </summary>
public sealed class ChannelArchitectureTests
{
    private static readonly Assembly AbstractionsAssembly = typeof(IAgentRuntime).Assembly;
    private static readonly Assembly CoreAssembly = typeof(ThalosBuilder).Assembly;
    private static readonly Assembly ChannelsAssembly = typeof(Thalos.Channels.ChannelPump).Assembly;
    private static readonly Assembly ChannelsTelegramAssembly = typeof(Thalos.Channels.Telegram.TelegramChannelAdapter).Assembly;

    // ArchUnitNET only knows the assemblies handed to LoadAssemblies: a rule over an assembly missing from this
    // array matches zero types and passes vacuously. Abstractions and Core are included so that Channels' own
    // dependencies resolve correctly, not because either is the subject of a rule here.
    private static readonly Assembly[] LoadedAssemblies = [AbstractionsAssembly, CoreAssembly, ChannelsAssembly, ChannelsTelegramAssembly];

    private static readonly ArchUnitNET.Domain.Architecture Arch = new ArchLoader().LoadAssemblies(LoadedAssemblies).Build();

    private static readonly IObjectProvider<IType> Channels = Types().That().ResideInAssembly(ChannelsAssembly).As("Channels");

    [Fact]
    public void Channels_types_are_a_non_empty_set()
    {
        // Guards every rule below that is phrased "Channels ... Should() ...": ResideInAssembly(ChannelsAssembly)
        // is the candidate set those rules run over, and an empty candidate set would let them pass unchecked.
        ChannelsAssembly.GetTypes().Should().NotBeEmpty();
    }

    [Fact]
    public void Channels_does_not_depend_on_Channels_Telegram_types() =>
        Types().That().Are(Channels).Should().NotDependOnAnyTypesThat().ResideInAssembly(ChannelsTelegramAssembly)
            .Check(Arch);

    [Fact]
    public void Channels_does_not_reference_the_Channels_Telegram_assembly()
    {
        // Stronger than the type-dependency rule above: a NotDependOn rule stays green while an unused reference
        // sits in the csproj, so the package graph itself — the actual isolation the split exists to provide — is
        // asserted here too, matching LayeringTests' "Skills_do_not_reference_the_memory_packages…" pattern.
        var referenced = Array.ConvertAll(ChannelsAssembly.GetReferencedAssemblies(), r => r.Name!);
        referenced.Should().NotBeEmpty("Channels references at least Thalos.NET.Abstractions and Thalos.NET");
        referenced.Should().NotContain(ChannelsTelegramAssembly.GetName().Name!);
    }

    [Fact]
    public void Channels_declares_no_direct_package_reference_outside_Microsoft_Extensions_or_ZeroAlloc()
    {
        // Deliberately over the DIRECT <PackageReference> items in the csproj, not the transitive assembly closure:
        // Thalos.NET.Channels project-references Thalos.NET, which itself pulls in Microsoft.Agents.AI, so a rule
        // phrased over the resolved dependency graph would fail on a dependency this package is supposed to have.
        var csprojPath = Path.Combine(RepoRoot(), "src", "Thalos.NET.Channels", "Thalos.NET.Channels.csproj");
        File.Exists(csprojPath).Should().BeTrue($"expected to find {csprojPath}");

        var packageReferences = XDocument.Load(csprojPath).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToArray();

        // A rule over an empty set would pass vacuously; prove there is something to check first.
        packageReferences.Should().NotBeEmpty("Thalos.NET.Channels.csproj should declare its Microsoft.Extensions.*/ZeroAlloc.* packages directly");

        packageReferences.Should().OnlyContain(name =>
            name!.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || name.StartsWith("ZeroAlloc.", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_public_type_in_Channels_and_Channels_Telegram_has_XML_documentation()
    {
        // GenerateDocumentationFile is on for every project, but CS1591 (missing XML doc) is suppressed repo-wide
        // (Directory.Build.props), so nothing at compile time enforces this — it is checked here instead by
        // cross-referencing each assembly's public surface against its own generated XML doc file.
        AssertEveryPublicTypeIsDocumented(ChannelsAssembly);
        AssertEveryPublicTypeIsDocumented(ChannelsTelegramAssembly);
    }

    private static void AssertEveryPublicTypeIsDocumented(Assembly assembly)
    {
        var publicTypes = assembly.GetExportedTypes();
        publicTypes.Should().NotBeEmpty($"{assembly.GetName().Name} should expose at least one public type");

        var docPath = Path.ChangeExtension(assembly.Location, ".xml");
        File.Exists(docPath).Should().BeTrue($"expected generated XML docs at {docPath}");

        var documentedIds = XDocument.Load(docPath).Descendants("member")
            .Select(m => m.Attribute("name")?.Value)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        var undocumented = publicTypes
            .Select(t => "T:" + t.FullName!.Replace('+', '.'))
            .Where(id => !documentedIds.Contains(id))
            .ToArray();

        undocumented.Should().BeEmpty($"every public type in {assembly.GetName().Name} must carry an XML doc comment");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Thalos.NET.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("expected to find Thalos.NET.slnx walking up from the test output directory");
        return dir!.FullName;
    }
}
