using System.Runtime.CompilerServices;
using Assembly = System.Reflection.Assembly;
using Type = System.Type;
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
    private static readonly Assembly SkillsAssembly = typeof(Thalos.Skills.SkillCatalogue).Assembly;
    private static readonly Assembly TestingAssembly = typeof(Thalos.Testing.ScriptedChatClient).Assembly;

    // ArchUnitNET only knows the assemblies handed to LoadAssemblies: a rule over an assembly that is missing
    // from this array matches zero types and passes vacuously. Every shipping assembly except Thalos.NET.Testing
    // is here, and the reflective sweeps below walk the same array so the two can never drift apart.
    private static readonly Assembly[] LoadedAssemblies =
    [
        AbstractionsAssembly,
        CoreAssembly,
        McpAssembly,
        AnthropicAssembly,
        SentinelAssembly,
        MemoryAssembly,
        RagNetAssembly,
        SkillsAssembly,
    ];

    private static readonly ArchUnitNET.Domain.Architecture Arch = new ArchLoader().LoadAssemblies(LoadedAssemblies).Build();

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
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(SkillsAssembly)
            .Check(Arch);

        Types().That().ResideInAssembly(SentinelAssembly).Or().ResideInAssembly(McpAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(MemoryAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(RagNetAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(SkillsAssembly)
            .Check(Arch);

        // The Rag.NET adapter is Memory + Rag.NET only: no Sentinel, Anthropic, MCP or Skills.
        Types().That().ResideInAssembly(RagNetAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(AnthropicAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(SkillsAssembly)
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
    public void Skills_do_not_depend_on_memory_ragnet_or_the_other_adapters() =>
        Types().That().ResideInAssembly(SkillsAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(MemoryAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(RagNetAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(SentinelAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(AnthropicAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInAssembly(McpAssembly)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(RagNetNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(NpgsqlNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SentinelNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnthropicNamespace)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(McpNamespace)
            .Check(Arch);

    [Fact]
    public void Memory_and_skills_do_not_depend_on_each_other() =>
        Types().That().ResideInAssembly(MemoryAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(SkillsAssembly)
            .Check(Arch);

    [Fact]
    public void Skills_do_not_reference_a_yaml_engine_or_any_third_party_parser()
    {
        var referenced = Array.ConvertAll(SkillsAssembly.GetReferencedAssemblies(), r => r.Name!);
        referenced.Should().NotContain(name =>
            name.Contains("Yaml", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Rag.NET", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Skills_do_not_reference_the_memory_packages_or_the_other_adapters()
    {
        // Stronger than the type-dependency rules above: a NotDependOn rule stays green while an unused
        // reference sits in the csproj, so the package graph itself is asserted here.
        var referenced = Array.ConvertAll(SkillsAssembly.GetReferencedAssemblies(), r => r.Name!);
        referenced.Should().NotContain(new[]
        {
            MemoryAssembly.GetName().Name!,
            RagNetAssembly.GetName().Name!,
            SentinelAssembly.GetName().Name!,
            AnthropicAssembly.GetName().Name!,
            McpAssembly.GetName().Name!,
            TestingAssembly.GetName().Name!,
        });
    }

    [Fact]
    public void Core_and_abstractions_do_not_reference_the_feature_packages()
    {
        var featureNames = new[] { MemoryAssembly.GetName().Name, RagNetAssembly.GetName().Name, SkillsAssembly.GetName().Name };
        var nonFeature = new[] { AbstractionsAssembly, CoreAssembly, SentinelAssembly, AnthropicAssembly, McpAssembly };
        for (var i = 0; i < nonFeature.Length; i++)
        {
            var referenced = Array.ConvertAll(nonFeature[i].GetReferencedAssemblies(), r => r.Name);
            referenced.Should().NotContain(featureNames, $"{nonFeature[i].GetName().Name} must not reference the feature packages");
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
        referenced.Should().NotContain(new[] { CoreAssembly.GetName().Name, McpAssembly.GetName().Name, AnthropicAssembly.GetName().Name, SentinelAssembly.GetName().Name, MemoryAssembly.GetName().Name, RagNetAssembly.GetName().Name, SkillsAssembly.GetName().Name, TestingAssembly.GetName().Name });
        referenced.Should().NotContain("Microsoft.Agents.AI");
    }

    /// <summary>
    /// AgentEvent.KindOf duplicates the wire name each subclass already holds in its Kind property, and
    /// AgentEventTests.AllEvents is a hand-maintained list that is not exhaustive. This sweeps every concrete
    /// AgentEvent in the loaded assemblies instead, so a new event with no KindOf branch cannot slip through.
    /// </summary>
    [Fact]
    public void Every_agent_event_has_a_KindOf_mapping_equal_to_its_instance_kind()
    {
        var eventTypes = ConcreteAgentEventTypes();
        var drift = new List<string>();
        for (var i = 0; i < eventTypes.Count; i++)
        {
            var eventType = eventTypes[i];
            var instanceKind = ((AgentEvent)RuntimeHelpers.GetUninitializedObject(eventType)).Kind;
            var mapped = MappedKindOrNull(eventType);
            if (mapped is null)
            {
                drift.Add($"{eventType.FullName}: KindOf has no branch for it (its Kind is '{instanceKind}')");
            }
            else if (!string.Equals(mapped, instanceKind, StringComparison.Ordinal))
            {
                drift.Add($"{eventType.FullName}: KindOf returns '{mapped}' but its Kind is '{instanceKind}'");
            }
        }

        drift.Should().BeEmpty("every concrete AgentEvent must have a KindOf mapping equal to its instance Kind");
    }

    /// <summary>
    /// Closes the loophole in the sweep above: a rule that walked only part of the event set would still pass it.
    /// Every AgentEventKinds constant must be claimed by exactly one discovered event, and no event may invent a
    /// kind that is not declared there — so the sweep provably sees all of them.
    /// </summary>
    [Fact]
    public void Every_declared_event_kind_is_claimed_by_exactly_one_agent_event()
    {
        var eventTypes = ConcreteAgentEventTypes();
        var discovered = new List<string>();
        for (var i = 0; i < eventTypes.Count; i++)
        {
            discovered.Add(((AgentEvent)RuntimeHelpers.GetUninitializedObject(eventTypes[i])).Kind);
        }

        discovered.Should().BeEquivalentTo(DeclaredEventKinds());
    }

    private static List<string> DeclaredEventKinds()
    {
        var declared = new List<string>();
        var fields = typeof(AgentEventKinds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].IsLiteral && fields[i].GetRawConstantValue() is string kind)
            {
                declared.Add(kind);
            }
        }

        return declared;
    }

    private static string? MappedKindOrNull(Type eventType)
    {
        try
        {
            return AgentEvent.KindOf(eventType);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<Type> ConcreteAgentEventTypes()
    {
        var found = new List<Type>();
        for (var i = 0; i < LoadedAssemblies.Length; i++)
        {
            var types = LoadedAssemblies[i].GetTypes();
            for (var j = 0; j < types.Length; j++)
            {
                if (!types[j].IsAbstract && typeof(AgentEvent).IsAssignableFrom(types[j]))
                {
                    found.Add(types[j]);
                }
            }
        }

        return found;
    }

    [Theory]
    [MemberData(nameof(NonTestingSourceAssemblies))]
    public void Shipping_assemblies_do_not_reference_test_frameworks(string assemblyName)
    {
        // Thalos.NET.Testing references xunit + AwesomeAssertions by design (it ships contract tests); nothing else may.
        var assembly = new[] { AbstractionsAssembly, CoreAssembly, McpAssembly, AnthropicAssembly, SentinelAssembly, MemoryAssembly, RagNetAssembly, SkillsAssembly }
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
        SkillsAssembly.GetName().Name!,
    };
}
