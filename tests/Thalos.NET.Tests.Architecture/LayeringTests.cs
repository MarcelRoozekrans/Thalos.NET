using Assembly = System.Reflection.Assembly;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Thalos.Tests.Architecture;

public sealed class LayeringTests
{
    private static readonly Assembly AbstractionsAssembly = typeof(IAgentRuntime).Assembly;
    private static readonly Assembly CoreAssembly = typeof(ThalosBuilder).Assembly;
    private static readonly Assembly McpAssembly = typeof(Thalos.Mcp.McpToolSource).Assembly;
    private static readonly Assembly AnthropicAssembly = typeof(Thalos.Anthropic.AnthropicChatClientProvider).Assembly;
    private static readonly Assembly SentinelAssembly = typeof(Thalos.Sentinel.SentinelChatClientDecorator).Assembly;
    private static readonly Assembly TestingAssembly = typeof(Thalos.Testing.ScriptedChatClient).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Arch = new ArchLoader().LoadAssemblies(
        AbstractionsAssembly,
        CoreAssembly,
        McpAssembly,
        AnthropicAssembly,
        SentinelAssembly).Build();

    // Abstractions and core share the root namespace "Thalos", so layers are partitioned by assembly, not namespace.
    private static readonly IObjectProvider<IType> Abstractions = Types().That().ResideInAssembly(AbstractionsAssembly).As("Abstractions");
    private static readonly IObjectProvider<IType> Core = Types().That().ResideInAssembly(CoreAssembly).As("Core");

    // Anchored so that "Anthropic" does not also match "Thalos.Anthropic" etc.
    private const string MafNamespace = @"^Microsoft\.Agents\.AI(\.|$)";
    private const string AnthropicNamespace = @"^Anthropic(\.|$)";
    private const string SentinelNamespace = @"^AI\.Sentinel(\.|$)";
    private const string McpNamespace = @"^ModelContextProtocol(\.|$)";

    [Fact]
    public void Abstractions_do_not_depend_on_MAF_or_providers() =>
        Types().That().Are(Abstractions).Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(MafNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnthropicNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SentinelNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(McpNamespace)
            .Check(Arch);

    [Fact]
    public void Core_does_not_depend_on_providers_or_sentinel_or_mcp() =>
        Types().That().Are(Core).Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnthropicNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SentinelNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(McpNamespace)
            .Check(Arch);

    [Fact]
    public void Adapters_do_not_depend_on_each_other() =>
        Types().That().ResideInAssembly(AnthropicAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .Check(Arch);

    [Fact]
    public void Reflection_is_confined_to_tool_discovery_and_policy_lookup() =>
        // full-name match so that nested types (LocalToolSource+ScopedTool holds the discovered MethodInfo) are covered too
        Types().That().Are(Core).And().DoNotHaveFullNameContaining("LocalToolSource").And().DoNotHaveFullNameContaining("DefaultToolAuthorizer")
            .Should().NotDependOnAnyTypesThat().HaveFullName("System.Reflection.MethodInfo")
            .Check(Arch);

    [Fact]
    public void Abstractions_do_not_reference_the_core_or_adapters()
    {
        var referenced = AbstractionsAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        referenced.Should().NotContain(new[] { CoreAssembly.GetName().Name, McpAssembly.GetName().Name, AnthropicAssembly.GetName().Name, SentinelAssembly.GetName().Name, TestingAssembly.GetName().Name });
        referenced.Should().NotContain("Microsoft.Agents.AI");
    }

    [Theory]
    [MemberData(nameof(NonTestingSourceAssemblies))]
    public void Shipping_assemblies_do_not_reference_test_frameworks(string assemblyName)
    {
        // Thalos.NET.Testing references xunit + AwesomeAssertions by design (it ships contract tests); nothing else may.
        var assembly = new[] { AbstractionsAssembly, CoreAssembly, McpAssembly, AnthropicAssembly, SentinelAssembly }
            .Single(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        referenced.Should().NotContain(name =>
            name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSubstitute", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AwesomeAssertions", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("FluentAssertions", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string> NonTestingSourceAssemblies() => new()
    {
        AbstractionsAssembly.GetName().Name!,
        CoreAssembly.GetName().Name!,
        McpAssembly.GetName().Name!,
        AnthropicAssembly.GetName().Name!,
        SentinelAssembly.GetName().Name!,
    };
}
