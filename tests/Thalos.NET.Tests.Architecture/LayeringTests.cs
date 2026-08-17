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
    private static readonly Assembly MemoryAssembly = typeof(Thalos.Memory.MemoryService).Assembly;
    private static readonly Assembly RagNetAssembly = typeof(Thalos.Memory.RagNet.RagNetMemoryIndex).Assembly;
    private static readonly Assembly TestingAssembly = typeof(Thalos.Testing.ScriptedChatClient).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Arch = new ArchLoader().LoadAssemblies(
        AbstractionsAssembly,
        CoreAssembly,
        McpAssembly,
        AnthropicAssembly,
        SentinelAssembly,
        MemoryAssembly,
        RagNetAssembly).Build();

    // Abstractions and core share the root namespace "Thalos", so layers are partitioned by assembly, not namespace.
    private static readonly IObjectProvider<IType> Abstractions = Types().That().ResideInAssembly(AbstractionsAssembly).As("Abstractions");
    private static readonly IObjectProvider<IType> Core = Types().That().ResideInAssembly(CoreAssembly).As("Core");

    // Anchored so that "Anthropic" does not also match "Thalos.Anthropic" etc.
    private const string MafNamespace = @"^Microsoft\.Agents\.AI(\.|$)";
    private const string AnthropicNamespace = @"^Anthropic(\.|$)";
    private const string SentinelNamespace = @"^AI\.Sentinel(\.|$)";
    private const string McpNamespace = @"^ModelContextProtocol(\.|$)";
    private const string RagNetNamespace = @"^Rag\.NET(\.|$)";
    private const string NpgsqlNamespace = @"^Npgsql(\.|$)";

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
    public void Adapters_do_not_depend_on_each_other()
    {
        Types().That().ResideInAssembly(AnthropicAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(MemoryAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(RagNetAssembly)
            .Check(Arch);

        // The Rag.NET adapter is Memory + Rag.NET only: no Sentinel, Anthropic or MCP.
        Types().That().ResideInAssembly(RagNetAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(AnthropicAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SentinelNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnthropicNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(McpNamespace)
            .Check(Arch);
    }

    [Fact]
    public void Memory_does_not_depend_on_RagNet_Npgsql_or_adapters() =>
        Types().That().ResideInAssembly(MemoryAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(RagNetNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(NpgsqlNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SentinelNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnthropicNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(McpNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(RagNetAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(AnthropicAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .Check(Arch);

    [Fact]
    public void Core_and_abstractions_do_not_reference_memory_packages()
    {
        var memoryNames = new[] { MemoryAssembly.GetName().Name, RagNetAssembly.GetName().Name };
        var nonMemory = new[] { AbstractionsAssembly, CoreAssembly, SentinelAssembly, AnthropicAssembly, McpAssembly };
        for (var i = 0; i < nonMemory.Length; i++)
        {
            var referenced = Array.ConvertAll(nonMemory[i].GetReferencedAssemblies(), r => r.Name);
            referenced.Should().NotContain(memoryNames, $"{nonMemory[i].GetName().Name} must not reference memory");
        }

        MemoryAssembly.GetReferencedAssemblies().Select(r => r.Name!).Should().NotContain(name =>
            name.StartsWith("Rag.NET", StringComparison.Ordinal) || name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

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
        referenced.Should().NotContain(new[] { CoreAssembly.GetName().Name, McpAssembly.GetName().Name, AnthropicAssembly.GetName().Name, SentinelAssembly.GetName().Name, MemoryAssembly.GetName().Name, RagNetAssembly.GetName().Name, TestingAssembly.GetName().Name });
        referenced.Should().NotContain("Microsoft.Agents.AI");
    }

    [Theory]
    [MemberData(nameof(NonTestingSourceAssemblies))]
    public void Shipping_assemblies_do_not_reference_test_frameworks(string assemblyName)
    {
        // Thalos.NET.Testing references xunit + AwesomeAssertions by design (it ships contract tests); nothing else may.
        var assembly = new[] { AbstractionsAssembly, CoreAssembly, McpAssembly, AnthropicAssembly, SentinelAssembly, MemoryAssembly, RagNetAssembly }
            .Single(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        referenced.Should().NotContain(name =>
            name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSubstitute", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AwesomeAssertions", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("FluentAssertions", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Thalos.NET.Testing", StringComparison.Ordinal));
    }

    public static TheoryData<string> NonTestingSourceAssemblies() => new()
    {
        AbstractionsAssembly.GetName().Name!,
        CoreAssembly.GetName().Name!,
        McpAssembly.GetName().Name!,
        AnthropicAssembly.GetName().Name!,
        SentinelAssembly.GetName().Name!,
        MemoryAssembly.GetName().Name!,
        RagNetAssembly.GetName().Name!,
    };
}
